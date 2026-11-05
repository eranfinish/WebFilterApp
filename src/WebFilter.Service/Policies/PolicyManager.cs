using System.Text.Json;
using WebFilter.Service.Storage;

namespace WebFilter.Service.Policies;

/// <summary>Reads and writes single registry values. <see cref="RegistryPolicyStore"/> in production, in memory in tests.</summary>
public interface IPolicyStore
{
    /// <summary>False when the hive is not available (a user who is not logged on has no loaded hive).</summary>
    bool IsAvailable(PolicyHive hive, string? userSid);
    object? Get(PolicyHive hive, string? userSid, string keyPath, string name);
    void Set(PolicyHive hive, string? userSid, string keyPath, string name, object value);
    void Delete(PolicyHive hive, string? userSid, string keyPath, string name);
}

/// <summary>
/// Keeps the browser policies in the registry equal to <see cref="BrowserPolicies.Desired"/>: writes missing or changed
/// values (including ones someone changed or deleted — tamper repair; only an administrator can write under
/// Software\Policies, so either is worth reporting), and deletes values it wrote earlier that are no longer wanted. It never
/// deletes values it neither wrote nor overwrote. What it wrote is remembered in a JSON file so removals survive restarts.
/// Users whose hive is not loaded are skipped and handled on a later pass.
/// <para>
/// The file is written only when the set changes, through <see cref="DurableFile"/>, with the previous version kept as a
/// backup. A file that cannot be read (zeros after a power cut — lab, 2026-11-05) does not stop the manager: it falls back
/// to the backup, or else starts from nothing and adopts the wanted values it finds, and reports either through
/// <paramref name="warn"/>. Starting from nothing forgets values it wrote that are no longer wanted.
/// </para>
/// </summary>
public sealed class PolicyManager(IPolicyStore store, string statePath, Action<string>? warn = null)
{
    /// <summary>The state file could not be read on the last load, so the next save must not back it up over a good backup.</summary>
    private bool _stateDamaged;

    /// <summary>
    /// The last load found neither the state file nor its backup readable, so values written earlier and no longer wanted
    /// are unknown (and an uninstall cannot remove them).
    /// </summary>
    public bool StartedOver { get; private set; }

    private sealed record Managed(PolicyHive Hive, string? UserSid, string KeyPath, string Name);

    /// <param name="Repaired">The values it had written that were found changed or deleted, and restored (registry paths).</param>
    public sealed record ApplyResult(int Written, int Deleted, int Skipped, IReadOnlyList<string> Repaired)
    {
        /// <summary>Whose values were restored: a user's SID, or null for machine-wide values; each once.</summary>
        public IReadOnlyList<string?> RepairedUsers { get; init; } = [];
    }

    public ApplyResult Apply(IReadOnlyList<PolicyValue> desired)
    {
        var managed = Load();
        var before = managed.ToHashSet();
        var wanted = new HashSet<Managed>();
        int written = 0, deleted = 0, skipped = 0;
        var repaired = new List<string>();
        var repairedUsers = new List<string?>();

        foreach (var value in desired)
        {
            var key = new Managed(value.Hive, value.UserSid, value.KeyPath, value.Name);
            wanted.Add(key);
            if (!store.IsAvailable(value.Hive, value.UserSid)) { skipped++; continue; }
            var current = store.Get(value.Hive, value.UserSid, value.KeyPath, value.Name);
            if (Equals(current, value.Value)) { managed.Add(key); continue; }
            if (managed.Contains(key))
            {
                repaired.Add(Describe(key));
                if (!repairedUsers.Contains(key.UserSid)) repairedUsers.Add(key.UserSid);
            }
            store.Set(value.Hive, value.UserSid, value.KeyPath, value.Name, value.Value);
            managed.Add(key);
            written++;
        }

        foreach (var stale in managed.Where(m => !wanted.Contains(m)).ToList())
        {
            if (!store.IsAvailable(stale.Hive, stale.UserSid)) continue;
            store.Delete(stale.Hive, stale.UserSid, stale.KeyPath, stale.Name);
            managed.Remove(stale);
            deleted++;
        }

        if (_stateDamaged || !managed.SetEquals(before)) Save(managed);
        return new ApplyResult(written, deleted, skipped, repaired) { RepairedUsers = repairedUsers };
    }

    /// <summary>Removes every value this manager wrote (uninstall). Users whose hive is not loaded keep theirs.</summary>
    public int RemoveAll() => Apply([]).Deleted;

    /// <summary>The values this manager wrote and has not removed (registry paths), e.g. what an uninstall left behind.</summary>
    public IReadOnlyList<string> Remaining() => Load().Select(Describe).Order(StringComparer.Ordinal).ToList();

    private static string Describe(Managed m) => $"{(m.Hive == PolicyHive.Machine ? "HKLM" : "HKU\\" + m.UserSid)}\\{m.KeyPath}\\{m.Name}";

    private HashSet<Managed> Load()
    {
        _stateDamaged = false;
        StartedOver = false;
        if (!File.Exists(statePath)) return [];
        if (TryRead(statePath) is { } current) return current;

        _stateDamaged = true;
        var backup = DurableFile.BackupPath(statePath);
        if (File.Exists(backup) && TryRead(backup) is { } previous)
        {
            warn?.Invoke($"{statePath} could not be read; using its backup from {File.GetLastWriteTime(backup):yyyy-MM-dd HH:mm}.");
            return previous;
        }
        warn?.Invoke($"{statePath} and its backup could not be read; starting again from the policies wanted now. " +
            "Values written earlier and no longer wanted may stay in the registry.");
        StartedOver = true;
        return [];
    }

    private static HashSet<Managed>? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<List<Managed>>(File.ReadAllText(path))?.ToHashSet() ?? [];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save(HashSet<Managed> managed)
    {
        var json = JsonSerializer.Serialize(managed.OrderBy(m => m.Hive).ThenBy(m => m.UserSid).ThenBy(m => m.KeyPath).ThenBy(m => m.Name).ToList());
        // A damaged file is replaced without becoming the backup, so a good backup survives.
        DurableFile.Write(statePath, json, keepBackup: !_stateDamaged);
        _stateDamaged = false;
    }
}

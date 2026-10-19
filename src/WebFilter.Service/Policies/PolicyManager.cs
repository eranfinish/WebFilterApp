using System.Text.Json;

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
/// values (including ones a user or program changed — tamper repair), and deletes values it wrote earlier that are no
/// longer wanted. It never deletes values it neither wrote nor overwrote. What it wrote is remembered in a JSON file so removals survive
/// restarts. Users whose hive is not loaded are skipped and handled on a later pass.
/// </summary>
public sealed class PolicyManager(IPolicyStore store, string statePath)
{
    private sealed record Managed(PolicyHive Hive, string? UserSid, string KeyPath, string Name);

    public sealed record ApplyResult(int Written, int Deleted, int Skipped, IReadOnlyList<string> Repaired);

    public ApplyResult Apply(IReadOnlyList<PolicyValue> desired)
    {
        var managed = Load();
        var wanted = new HashSet<Managed>();
        int written = 0, deleted = 0, skipped = 0;
        var repaired = new List<string>();

        foreach (var value in desired)
        {
            var key = new Managed(value.Hive, value.UserSid, value.KeyPath, value.Name);
            wanted.Add(key);
            if (!store.IsAvailable(value.Hive, value.UserSid)) { skipped++; continue; }
            var current = store.Get(value.Hive, value.UserSid, value.KeyPath, value.Name);
            if (Equals(current, value.Value)) { managed.Add(key); continue; }
            if (current is not null && managed.Contains(key)) repaired.Add(Describe(key));
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

        Save(managed);
        return new ApplyResult(written, deleted, skipped, repaired);
    }

    /// <summary>Removes every value this manager wrote (uninstall). Users whose hive is not loaded keep theirs.</summary>
    public int RemoveAll() => Apply([]).Deleted;

    private static string Describe(Managed m) => $"{(m.Hive == PolicyHive.Machine ? "HKLM" : "HKU\\" + m.UserSid)}\\{m.KeyPath}\\{m.Name}";

    private HashSet<Managed> Load()
    {
        if (!File.Exists(statePath)) return [];
        return JsonSerializer.Deserialize<List<Managed>>(File.ReadAllText(statePath))?.ToHashSet() ?? [];
    }

    private void Save(HashSet<Managed> managed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        var temp = statePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(managed.OrderBy(m => m.Hive).ThenBy(m => m.UserSid).ThenBy(m => m.KeyPath).ThenBy(m => m.Name).ToList()));
        File.Move(temp, statePath, overwrite: true);
    }
}

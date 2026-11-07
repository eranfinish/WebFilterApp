using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;
using WebFilter.Service.Mapping;
using WebFilter.Service.Storage;

namespace WebFilter.Service.Hub;

/// <summary>
/// The hub's automatic backups of the family's settings (the same file as the admin app's "save a backup",
/// <see cref="FamilyBackupFormat"/>) into a folder a parent chose — typically inside their OneDrive or Google Drive folder,
/// so the copy leaves the computer by itself. Written when the settings themselves changed (usage, requests and the version
/// alone do not count): <see cref="LatestFileName"/>, and one dated copy per day kept for <see cref="KeepDays"/> days. The
/// folder and the last result are kept in <c>auto-backup.json</c> in the data folder.
/// </summary>
public sealed partial class AutoBackup(FamilyService family, ILogger<AutoBackup> log)
{
    public const string LatestFileName = "WebFilter-backup-latest.json";

    public const int KeepDays = 30;

    private const string StateFileName = "auto-backup.json";

    private readonly object _gate = new();
    private State? _state;

    private sealed record State(string? Folder, DateTimeOffset? LastBackupAt, string? LastHash, string? LastError);

    /// <summary>"WebFilter-backup-2026-11-07.json": the day's copy.</summary>
    public static string DailyFileName(DateOnly day) => $"WebFilter-backup-{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json";

    [GeneratedRegex(@"^WebFilter-backup-(\d{4}-\d{2}-\d{2})\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DailyFile();

    private string StatePath => Path.Combine(family.Options.DataDirectory, StateFileName);

    public AutoBackupResult Status()
    {
        lock (_gate)
        {
            var state = Load();
            return new AutoBackupResult(state.Folder, state.LastBackupAt, state.LastError);
        }
    }

    /// <summary>
    /// Turns automatic backups on into <paramref name="folder"/> (after writing one there) or off (null). Returns null when
    /// done, otherwise the refusal (one of <see cref="SetAutoBackup"/>'s codes); a refused folder changes nothing.
    /// </summary>
    public AdminError? Configure(string? folder)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                Save(Load() with { Folder = null, LastError = null });
                log.LogInformation("Automatic backups turned off");
                return null;
            }
            folder = folder.Trim();
            if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal))
                return new AdminError(SetAutoBackup.NetworkCode, "Network folders are not supported; choose a folder on this computer.");
            if (!Path.IsPathFullyQualified(folder)) return new AdminError(SetAutoBackup.InvalidCode, "The folder must be a full path on this computer.");
            folder = Path.GetFullPath(folder);
            if (!Directory.Exists(folder)) return new AdminError(SetAutoBackup.MissingCode, $"The folder {folder} does not exist.");
            try
            {
                var written = Write(folder);
                Save(new State(folder, family.Now, written, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new AdminError(SetAutoBackup.WriteFailedCode, ex.Message);
            }
            log.LogInformation("Automatic backups go to {Folder}", folder);
            return null;
        }
    }

    /// <summary>
    /// Writes a backup when backups are on and the settings changed since the last one (or the latest file is gone). A failure
    /// is kept for the admin app and logged once; the next call tries again. Returns whether a backup was written.
    /// </summary>
    public bool RunIfChanged()
    {
        lock (_gate)
        {
            var state = Load();
            if (state.Folder is not { } folder) return false;
            try
            {
                if (state.LastError is null && Hash(Current()) == state.LastHash && File.Exists(Path.Combine(folder, LatestFileName))) return false;
                var written = Write(folder);
                Save(state with { LastBackupAt = family.Now, LastHash = written, LastError = null });
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (state.LastError != ex.Message) log.LogWarning(ex, "Automatic backup to {Folder} failed; trying again later", folder);
                Save(state with { LastError = ex.Message });
                return false;
            }
        }
    }

    private FamilyBundle Current() => family.WithHub((hub, _) => BundleMapper.ToBundle(hub.Config, hub.Version));

    /// <summary>The settings without their version, which also moves with usage and requests.</summary>
    private static string Hash(FamilyBundle settings) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FamilyBundleJson.Serialize(settings with { Version = 0 }))));

    /// <summary>Writes the latest file and today's copy, removes our dated copies past <see cref="KeepDays"/>; returns the settings' hash.</summary>
    private string Write(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"The folder {folder} no longer exists.");
        var settings = Current();
        var now = family.Now;
        var text = FamilyBackupFormat.Serialize(new FamilyBackupFile(now, family.DeviceId, AppVersion, settings));
        DurableFile.Write(Path.Combine(folder, LatestFileName), text);
        var today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        DurableFile.Write(Path.Combine(folder, DailyFileName(today)), text);
        foreach (var file in Directory.EnumerateFiles(folder, "WebFilter-backup-*.json"))
        {
            var match = DailyFile().Match(Path.GetFileName(file));
            if (match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day < today.AddDays(-KeepDays))
                File.Delete(file);
        }
        return Hash(settings);
    }

    private static string? AppVersion =>
        typeof(AutoBackup).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;

    private State Load()
    {
        if (_state is not null) return _state;
        try
        {
            if (File.Exists(StatePath) && JsonNode.Parse(File.ReadAllText(StatePath)) is JsonObject json)
            {
                var at = (string?)json["lastBackupAt"];
                return _state = new State((string?)json["folder"],
                    DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null,
                    (string?)json["lastHash"], (string?)json["lastError"]);
            }
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "{File} could not be read; automatic backups are off until a parent sets a folder again", StatePath);
        }
        return _state = new State(null, null, null, null);
    }

    private void Save(State state)
    {
        _state = state;
        var json = new JsonObject
        {
            ["folder"] = state.Folder,
            ["lastBackupAt"] = state.LastBackupAt?.ToString("O", CultureInfo.InvariantCulture),
            ["lastHash"] = state.LastHash,
            ["lastError"] = state.LastError,
        };
        DurableFile.Write(StatePath, json.ToJsonString());
    }
}

/// <summary>
/// Drives <see cref="AutoBackup"/> on the hub: a minute after the settings change (several changes in a row make one
/// backup), and every <see cref="Interval"/> to retry a failure or replace a deleted file.
/// </summary>
public sealed class AutoBackupService(FamilyService family, AutoBackup backup, ILogger<AutoBackupService> log) : BackgroundService
{
    internal TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    internal TimeSpan Settle { get; set; } = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _wake = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!family.IsHub) return;
        void OnChanged(int _) => _wake.Release();
        family.SettingsChanged += OnChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (await _wake.WaitAsync(Interval, stoppingToken))
                {
                    await Task.Delay(Settle, stoppingToken);
                    while (_wake.CurrentCount > 0) _wake.Wait(0, stoppingToken);
                }
                try { backup.RunIfChanged(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "Automatic backup failed"); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            family.SettingsChanged -= OnChanged;
        }
    }
}

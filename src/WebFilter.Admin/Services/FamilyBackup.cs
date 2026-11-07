using System.Globalization;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.Services;

/// <summary>
/// Backups of the family's settings in the admin app: the file (<see cref="FamilyBackupFormat"/>, shared with the hub's
/// automatic backups), its suggested name, and how a backup's settings fit the hub they are restored on.
/// </summary>
public static class FamilyBackup
{
    /// <summary>"WebFilter-backup-2026-11-07.json", by the family's local date.</summary>
    public static string SuggestedFileName(DateTimeOffset localNow) =>
        $"WebFilter-backup-{localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.json";

    public static string Serialize(FamilyBackupFile backup) => FamilyBackupFormat.Serialize(backup);

    /// <exception cref="FamilyBackupException">Not a WebFilter backup, written by a newer version, or damaged.</exception>
    public static FamilyBackupFile Read(string json) => FamilyBackupFormat.Read(json);

    /// <summary>
    /// The backup's settings, made to fit the hub they are restored on (<paramref name="thisDeviceId"/>, whose current settings
    /// are <paramref name="current"/>): this computer stays the hub. Windows account ids belong to a computer, so the links
    /// follow the computer, not the role:
    /// <list type="bullet">
    /// <item>The same hub (a reinstall, a repaired PC): its entry comes back as it was.</item>
    /// <item>A computer that was an agent in the backup becomes the hub with its own links.</item>
    /// <item>Another computer becomes the hub: its accounts are as Windows reports them, unlinked, and the parents answer for
    /// them on the Devices screen. The old hub's entry stays, as an agent's, so pairing it later brings its links back.</item>
    /// </list>
    /// Accounts the hub knows now and the backup does not are kept (unlinked).
    /// </summary>
    public static FamilyBundle ForThisHub(FamilyBackupFile backup, FamilyBundle current, string thisDeviceId)
    {
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrEmpty(thisDeviceId);
        var reported = current.Devices.FirstOrDefault(d => d.Id == thisDeviceId)?.Accounts ?? [];
        var saved = backup.Settings.Devices.FirstOrDefault(d => d.Id == thisDeviceId);
        var accounts = (saved?.Accounts ?? []).ToList();
        accounts.AddRange(reported
            .Where(a => accounts.All(s => !string.Equals(s.AccountId, a.AccountId, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a with { Member = null, Guest = false }));
        var hub = new DeviceDto(thisDeviceId, saved?.Os ?? "windows", "hub", accounts);
        var others = backup.Settings.Devices.Where(d => d.Id != thisDeviceId).Select(d => d.Role == "hub" ? d with { Role = "agent" } : d);
        return backup.Settings with { Version = current.Version, Devices = [hub, .. others] };
    }
}

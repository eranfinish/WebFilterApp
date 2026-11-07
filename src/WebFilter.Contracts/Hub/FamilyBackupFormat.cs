using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WebFilter.Contracts.Hub;

/// <summary>
/// A copy of the family's settings in a file the parents keep (the admin app's "save a backup", and the hub's automatic
/// backups): what the hub stores in its settings — members, hours, limits, rules, channels, apps, categories, computers and
/// the Windows accounts linked on each — so a broken or replaced parents' computer can get the family back. Not in it: the
/// activity log, requests, usage and pairings (computers are paired again).
/// </summary>
/// <param name="CreatedAt">When it was saved.</param>
/// <param name="HubDeviceId">The hub computer the settings were saved on (its id in <see cref="FamilyBundle.Devices"/>).</param>
/// <param name="AppVersion">The version of the program that wrote it, for the record.</param>
public sealed record FamilyBackupFile(DateTimeOffset CreatedAt, string HubDeviceId, string? AppVersion, FamilyBundle Settings);

public enum BackupProblem
{
    /// <summary>Not JSON, or not a WebFilter backup.</summary>
    NotABackup,

    /// <summary>Written by a newer WebFilter, in a layout this one cannot read.</summary>
    TooNew,

    /// <summary>A WebFilter backup with parts missing or damaged.</summary>
    Damaged,
}

/// <summary>A file that cannot be restored; <see cref="Problem"/> says why, for the parent's wording.</summary>
public sealed class FamilyBackupException(BackupProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public BackupProblem Problem { get; } = problem;
}

/// <summary>The file layout: <c>{ format, formatVersion, createdAt, hubDeviceId, appVersion, settings }</c>, indented JSON.</summary>
public static class FamilyBackupFormat
{
    public const string Format = "webfilter-family-backup";

    /// <summary>Raised when the file layout changes in a way older programs cannot read.</summary>
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static string Serialize(FamilyBackupFile backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var file = new JsonObject
        {
            ["format"] = Format,
            ["formatVersion"] = FormatVersion,
            ["createdAt"] = backup.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["hubDeviceId"] = backup.HubDeviceId,
            ["appVersion"] = backup.AppVersion,
            ["settings"] = JsonNode.Parse(FamilyBundleJson.Serialize(backup.Settings)),
        };
        return file.ToJsonString(Indented) + Environment.NewLine;
    }

    /// <exception cref="FamilyBackupException">Not a WebFilter backup, written by a newer version, or damaged.</exception>
    public static FamilyBackupFile Read(string json)
    {
        JsonObject? file;
        try
        {
            file = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException ex)
        {
            throw new FamilyBackupException(BackupProblem.NotABackup, "The file is not JSON.", ex);
        }
        if (file is null || Text(file, "format") != Format) throw new FamilyBackupException(BackupProblem.NotABackup, "The file is not a WebFilter backup.");
        if (file["formatVersion"] is not JsonValue version || !version.TryGetValue<int>(out var number))
            throw new FamilyBackupException(BackupProblem.Damaged, "The backup has no format version.");
        if (number > FormatVersion)
            throw new FamilyBackupException(BackupProblem.TooNew, $"The backup's format {number} is newer than {FormatVersion}.");
        if (!DateTimeOffset.TryParse(Text(file, "createdAt"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var createdAt)
            || Text(file, "hubDeviceId") is not { Length: > 0 } hub || file["settings"] is not JsonObject settings)
            throw new FamilyBackupException(BackupProblem.Damaged, "The backup is incomplete.");
        FamilyBundle? bundle;
        try
        {
            bundle = FamilyBundleJson.Deserialize(settings.ToJsonString());
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new FamilyBackupException(BackupProblem.Damaged, "The backup's settings are damaged.", ex);
        }
        if (bundle is null || string.IsNullOrWhiteSpace(bundle.TimeZone))
            throw new FamilyBackupException(BackupProblem.Damaged, "The backup's settings are empty.");
        return new FamilyBackupFile(createdAt, hub, Text(file, "appVersion"), bundle);
    }

    private static string? Text(JsonObject file, string name) =>
        file[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

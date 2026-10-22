using Dapper;

namespace WebFilter.Service.Storage;

/// <param name="RevokedReason">Set when the device revoked itself (<see cref="Contracts.Hub.UnregisterRequest.Reasons"/>); null when a parent did.</param>
public sealed record PairedDevice(string Id, string Name, string Os, string CertThumbprint, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt, DateTimeOffset? RevokedAt,
    string? RevokedReason = null);

/// <summary>Hub-side registry of paired devices and received event ids (hub schemas 002 and 004).</summary>
public sealed class DeviceStore(SqliteDatabase database)
{
    public void Register(string id, string name, string os, string thumbprint, DateTimeOffset now)
    {
        using var db = database.Open();
        db.Execute("""
            INSERT INTO devices (id, name, os, cert_thumbprint, paired_at) VALUES (@id, @name, @os, @thumbprint, @now)
            ON CONFLICT(id) DO UPDATE SET name = @name, os = @os, cert_thumbprint = @thumbprint, paired_at = @now, revoked_at = NULL, revoked_reason = NULL
            """, new { id, name, os, thumbprint, now = StateJson.Instant(now) });
    }

    public PairedDevice? ByThumbprint(string thumbprint) => All().FirstOrDefault(d => string.Equals(d.CertThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<PairedDevice> All()
    {
        using var db = database.Open();
        return db.Query<(string Id, string Name, string Os, string Thumb, string Paired, string? Seen, string? Revoked, string? Reason)>(
                "SELECT id, name, os, cert_thumbprint, paired_at, last_seen_at, revoked_at, revoked_reason FROM devices ORDER BY paired_at")
            .Select(d => new PairedDevice(d.Id, d.Name, d.Os, d.Thumb, StateJson.Instant(d.Paired),
                d.Seen is null ? null : StateJson.Instant(d.Seen), d.Revoked is null ? null : StateJson.Instant(d.Revoked), d.Reason))
            .ToList();
    }

    public void Touch(string id, DateTimeOffset now)
    {
        using var db = database.Open();
        db.Execute("UPDATE devices SET last_seen_at = @now WHERE id = @id", new { id, now = StateJson.Instant(now) });
    }

    /// <param name="reason">Null when a parent revokes the device; the device's own reason when it unregisters itself.</param>
    /// <returns>False when there is no such device or it was already revoked.</returns>
    public bool Revoke(string id, DateTimeOffset now, string? reason = null)
    {
        using var db = database.Open();
        return db.Execute("UPDATE devices SET revoked_at = @now, revoked_reason = @reason WHERE id = @id AND revoked_at IS NULL",
            new { id, now = StateJson.Instant(now), reason }) > 0;
    }

    /// <summary>Records an agent event id; false if it was already received (uploads are retried).</summary>
    public bool FirstTimeSeen(string deviceId, string eventId, DateTimeOffset now)
    {
        using var db = database.Open();
        return db.Execute("INSERT OR IGNORE INTO seen_events (device_id, event_id, seen_at) VALUES (@deviceId, @eventId, @now)",
            new { deviceId, eventId, now = StateJson.Instant(now) }) > 0;
    }
}

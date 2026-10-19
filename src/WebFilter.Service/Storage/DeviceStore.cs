using Dapper;

namespace WebFilter.Service.Storage;

public sealed record PairedDevice(string Id, string Name, string Os, string CertThumbprint, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt, DateTimeOffset? RevokedAt);

/// <summary>Hub-side registry of paired devices and received event ids (hub schema 002).</summary>
public sealed class DeviceStore(SqliteDatabase database)
{
    public void Register(string id, string name, string os, string thumbprint, DateTimeOffset now)
    {
        using var db = database.Open();
        db.Execute("""
            INSERT INTO devices (id, name, os, cert_thumbprint, paired_at) VALUES (@id, @name, @os, @thumbprint, @now)
            ON CONFLICT(id) DO UPDATE SET name = @name, os = @os, cert_thumbprint = @thumbprint, paired_at = @now, revoked_at = NULL
            """, new { id, name, os, thumbprint, now = StateJson.Instant(now) });
    }

    public PairedDevice? ByThumbprint(string thumbprint) => All().FirstOrDefault(d => string.Equals(d.CertThumbprint, thumbprint, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<PairedDevice> All()
    {
        using var db = database.Open();
        return db.Query<(string Id, string Name, string Os, string Thumb, string Paired, string? Seen, string? Revoked)>(
                "SELECT id, name, os, cert_thumbprint, paired_at, last_seen_at, revoked_at FROM devices ORDER BY paired_at")
            .Select(d => new PairedDevice(d.Id, d.Name, d.Os, d.Thumb, StateJson.Instant(d.Paired),
                d.Seen is null ? null : StateJson.Instant(d.Seen), d.Revoked is null ? null : StateJson.Instant(d.Revoked)))
            .ToList();
    }

    public void Touch(string id, DateTimeOffset now)
    {
        using var db = database.Open();
        db.Execute("UPDATE devices SET last_seen_at = @now WHERE id = @id", new { id, now = StateJson.Instant(now) });
    }

    public bool Revoke(string id, DateTimeOffset now)
    {
        using var db = database.Open();
        return db.Execute("UPDATE devices SET revoked_at = @now WHERE id = @id AND revoked_at IS NULL", new { id, now = StateJson.Instant(now) }) > 0;
    }

    /// <summary>Records an agent event id; false if it was already received (uploads are retried).</summary>
    public bool FirstTimeSeen(string deviceId, string eventId, DateTimeOffset now)
    {
        using var db = database.Open();
        return db.Execute("INSERT OR IGNORE INTO seen_events (device_id, event_id, seen_at) VALUES (@deviceId, @eventId, @now)",
            new { deviceId, eventId, now = StateJson.Instant(now) }) > 0;
    }
}

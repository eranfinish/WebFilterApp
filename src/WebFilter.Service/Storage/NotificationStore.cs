using Dapper;
using WebFilter.Contracts.Notify;

namespace WebFilter.Service.Storage;

/// <summary>
/// The hub's parent notifications (hub schema 005, PROTECTION-SPEC §10), read by the notifier through the Notify pipe.
/// Each has a stable id, so recording the same one again (a retried upload, a restart) does nothing; its <c>at</c> is
/// when the hub recorded it, kept strictly increasing so that "after the last one I saw" is an exact cursor.
/// </summary>
public sealed class NotificationStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly SqliteDatabase _database;
    private readonly object _gate = new();
    private long _lastTicks;

    public NotificationStore(SqliteDatabase database)
    {
        _database = database;
        using var db = database.Open();
        _lastTicks = db.ExecuteScalar<long?>("SELECT MAX(at) FROM notifications") ?? 0;
    }

    /// <summary>Records a notification unless one with this id exists. Returns whether it was new.</summary>
    public bool Add(string id, string kind, DateTimeOffset now, string? memberId = null, string? deviceId = null, string? detail = null)
    {
        lock (_gate)
        {
            using var db = _database.Open();
            if (db.ExecuteScalar<long>("SELECT COUNT(*) FROM notifications WHERE id = @id", new { id }) > 0) return false;
            var at = Math.Max(now.UtcTicks, _lastTicks + 1);
            db.Execute("INSERT INTO notifications (id, kind, at, member_id, device_id, detail) VALUES (@id, @kind, @at, @memberId, @deviceId, @detail)",
                new { id, kind, at, memberId, deviceId, detail });
            _lastTicks = at;
            return true;
        }
    }

    public bool Exists(string id)
    {
        using var db = _database.Open();
        return db.ExecuteScalar<long>("SELECT COUNT(*) FROM notifications WHERE id = @id", new { id }) > 0;
    }

    /// <summary>Notifications recorded after <paramref name="since"/> (all when null), oldest first, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<NotificationDto> Since(DateTimeOffset? since, int max = NotificationsResult.MaxItems)
    {
        using var db = _database.Open();
        return db.Query<(string Id, string Kind, long At, string? Member, string? Device, string? Detail)>(
                "SELECT id, kind, at, member_id, device_id, detail FROM notifications WHERE at > @after ORDER BY at LIMIT @max",
                new { after = since?.UtcTicks ?? 0, max })
            .Select(n => new NotificationDto(n.Id, n.Kind, new DateTimeOffset(n.At, TimeSpan.Zero), n.Member, n.Device, n.Detail))
            .ToList();
    }

    /// <summary>Deletes notifications recorded before <paramref name="cutoff"/>.</summary>
    public void Prune(DateTimeOffset cutoff)
    {
        using var db = _database.Open();
        db.Execute("DELETE FROM notifications WHERE at < @cutoff", new { cutoff = cutoff.UtcTicks });
    }
}

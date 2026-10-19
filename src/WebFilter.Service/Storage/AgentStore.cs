using Dapper;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Engine;
using WebFilter.Core.Time;
using WebFilter.Service.Mapping;

namespace WebFilter.Service.Storage;

/// <summary>Persists <see cref="AgentState"/>: the last hub snapshot, local usage, and what still has to reach the hub.</summary>
public sealed class AgentStore(SqliteDatabase database)
{
    private int? _savedSnapshotVersion;
    private readonly HashSet<(string, string, long)> _savedShared = [];
    private readonly HashSet<(string, long)> _savedLocal = [];

    public AgentState? Load(string deviceId)
    {
        using var db = database.Open();
        var counters = db.Query<(string Name, long Value)>("SELECT name, value FROM counters").ToDictionary(c => c.Name, c => (int)c.Value);
        var row = db.QueryFirstOrDefault<(long Version, string Bundle, string Runtime)>("SELECT version, bundle_json, runtime_json FROM snapshot WHERE id = 1");

        HubSnapshot? snapshot = null;
        if (row.Bundle is not null)
        {
            var shared = new UsageLedger();
            foreach (var (member, device, minute) in db.Query<(string, string, long)>("SELECT member_id, device_id, minute FROM shared_usage"))
            {
                shared.Mark(member, device, StateJson.FromUnixMinute(minute));
                _savedShared.Add((member, device, minute));
            }
            snapshot = new HubSnapshot((int)row.Version, BundleMapper.ToConfig(FamilyBundleJson.Deserialize(row.Bundle)), StateJson.Runtime(row.Runtime), shared);
            _savedSnapshotVersion = (int)row.Version;
        }

        var local = new UsageLedger();
        foreach (var (member, minute) in db.Query<(string, long)>("SELECT member_id, minute FROM local_usage"))
        {
            local.Mark(member, deviceId, StateJson.FromUnixMinute(minute));
            _savedLocal.Add((member, minute));
        }

        var pauses = db.Query<(string Member, string Until)>("SELECT member_id, until FROM local_pauses").ToDictionary(p => p.Member, p => StateJson.Instant(p.Until));
        var outbox = db.Query<string>("SELECT event_json FROM outbox ORDER BY seq").Select(StateJson.Event).ToList();
        var queued = db.Query<string>("SELECT request_json FROM queued_requests ORDER BY id").Select(StateJson.Request).ToList();

        if (snapshot is null && local.Entries().Any() is false && outbox.Count == 0 && queued.Count == 0) return null;
        return new AgentState(deviceId, snapshot, local, pauses, outbox, queued, counters.GetValueOrDefault("queued"));
    }

    public void Save(AgentState state)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();

        if (state.Snapshot is { } snapshot && snapshot.Version != _savedSnapshotVersion)
        {
            db.Execute("INSERT OR REPLACE INTO snapshot (id, version, bundle_json, runtime_json) VALUES (1, @v, @bundle, @runtime)",
                new
                {
                    v = snapshot.Version,
                    bundle = FamilyBundleJson.Serialize(BundleMapper.ToBundle(snapshot.Config, snapshot.Version)),
                    runtime = StateJson.Runtime(snapshot.Runtime),
                }, tx);
            foreach (var (member, device, minute) in snapshot.Usage.Entries())
            {
                var key = (member, device, StateJson.ToUnixMinute(minute));
                if (_savedShared.Add(key))
                    db.Execute("INSERT OR IGNORE INTO shared_usage (member_id, device_id, minute) VALUES (@member, @device, @minute)", new { member, device, minute = key.Item3 }, tx);
            }
            _savedSnapshotVersion = snapshot.Version;
        }

        foreach (var (member, _, minute) in state.LocalUsage.Entries())
        {
            var key = (member, StateJson.ToUnixMinute(minute));
            if (_savedLocal.Add(key))
                db.Execute("INSERT OR IGNORE INTO local_usage (member_id, minute) VALUES (@member, @minute)", new { member, minute = key.Item2 }, tx);
        }

        db.Execute("DELETE FROM local_pauses", transaction: tx);
        foreach (var (member, until) in state.LocalPauses)
            db.Execute("INSERT INTO local_pauses (member_id, until) VALUES (@member, @until)", new { member, until = StateJson.Instant(until) }, tx);

        // Outbox and queue are small and replaced whole: what is persisted is exactly what has not reached the hub.
        db.Execute("DELETE FROM outbox; DELETE FROM queued_requests;", transaction: tx);
        foreach (var e in state.Outbox) db.Execute("INSERT INTO outbox (event_json) VALUES (@json)", new { json = StateJson.Event(e) }, tx);
        foreach (var r in state.QueuedRequests) db.Execute("INSERT INTO queued_requests (id, request_json) VALUES (@id, @json)", new { id = r.Id, json = StateJson.Request(r) }, tx);

        db.Execute("INSERT OR REPLACE INTO counters (name, value) VALUES ('queued', @value)", new { value = state.QueuedCounter }, tx);
        tx.Commit();
    }
}

using Dapper;
using WebFilter.Core.Decisions;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Engine;
using WebFilter.Core.Time;
using WebFilter.Service.Mapping;

namespace WebFilter.Service.Storage;

/// <summary>
/// Persists <see cref="HubState"/>. <see cref="Save"/> is incremental: the settings document is written only when it
/// changed, and only usage minutes and events not yet stored are inserted.
/// </summary>
public sealed class HubStore(SqliteDatabase database)
{
    public static readonly TimeSpan UsageRetention = TimeSpan.FromDays(35);
    public static readonly TimeSpan EventRetention = TimeSpan.FromDays(90);

    private string? _lastBundleJson;
    private readonly HashSet<(string, string, long)> _savedUsage = [];
    private FamilyEvent? _lastSavedEvent;

    public HubState? Load()
    {
        using var db = database.Open();
        var config = db.QueryFirstOrDefault<(long Version, string Json)>("SELECT version, bundle_json FROM config ORDER BY version DESC LIMIT 1");
        if (config.Json is null) return null;

        var runtime = db.Query<(string Member, string? Paused, string? Homework, string Grants)>("SELECT member_id, paused_until, homework_until, grants_json FROM runtime")
            .ToDictionary(r => r.Member, r => new MemberRuntime
            {
                PausedUntil = r.Paused is null ? null : StateJson.Instant(r.Paused),
                HomeworkUntil = r.Homework is null ? null : StateJson.Instant(r.Homework),
                Grants = StateJson.Grants(r.Grants),
            });

        var usage = new UsageLedger();
        foreach (var (member, device, minute) in db.Query<(string, string, long)>("SELECT member_id, device_id, minute FROM usage_minutes"))
        {
            usage.Mark(member, device, StateJson.FromUnixMinute(minute));
            _savedUsage.Add((member, device, minute));
        }

        var events = db.Query<(string Ts, string Type, string? Member, string? Device, string? Reason, string? Url, string? App, long? Remaining, string? Detail, string? Admin)>(
                "SELECT ts, type, member_id, device_id, reason, url, app, remaining, detail, admin FROM events ORDER BY seq")
            .Select(e => new FamilyEvent(Enum.Parse<EventType>(e.Type), StateJson.Instant(e.Ts), e.Member, e.Device,
                e.Reason is null ? ReasonCode.None : ReasonCodes.Parse(e.Reason), e.Url, e.App, (int?)e.Remaining, e.Detail, e.Admin))
            .ToList();
        _lastSavedEvent = events.LastOrDefault();

        var requests = db.Query<(string Id, string Member, string Device, string Kind, string Target, string Key, long? Minutes, string? Message, string Created, string Status, string? Decided, string? Rule)>(
                "SELECT id, member_id, device_id, kind, target, target_key, minutes, message, created_at, status, decided_at, created_rule_id FROM access_requests ORDER BY created_at")
            .Select(r => new AccessRequest
            {
                Id = r.Id, MemberId = r.Member, DeviceId = r.Device, Kind = Enum.Parse<RequestKind>(r.Kind), Target = r.Target, TargetKey = r.Key,
                Minutes = (int?)r.Minutes, Message = r.Message, CreatedAt = StateJson.Instant(r.Created), Status = Enum.Parse<RequestStatus>(r.Status),
                DecidedAt = r.Decided is null ? null : StateJson.Instant(r.Decided), CreatedRuleId = r.Rule,
            }).ToList();

        var counters = db.Query<(string Name, long Value)>("SELECT name, value FROM counters").ToDictionary(c => c.Name, c => (int)c.Value);
        _lastBundleJson = config.Json;
        var bundle = FamilyBundleJson.Deserialize(config.Json);
        return new HubState(counters.GetValueOrDefault("version", (int)config.Version), BundleMapper.ToConfig(bundle), runtime, usage, events, requests,
            counters.GetValueOrDefault("requests"), counters.GetValueOrDefault("rules"));
    }

    public void Save(HubState state, DateTimeOffset now)
    {
        using var db = database.Open();
        using var tx = db.BeginTransaction();

        var bundleJson = FamilyBundleJson.Serialize(BundleMapper.ToBundle(state.Config, 0));
        if (bundleJson != _lastBundleJson)
        {
            db.Execute("INSERT OR REPLACE INTO config (version, bundle_json, saved_at) VALUES (@v, @json, @at)",
                new { v = state.Version, json = bundleJson, at = StateJson.Instant(now) }, tx);
            _lastBundleJson = bundleJson;
        }

        db.Execute("DELETE FROM runtime", transaction: tx);
        foreach (var (member, runtime) in state.Runtime)
            db.Execute("INSERT INTO runtime (member_id, paused_until, homework_until, grants_json) VALUES (@member, @paused, @homework, @grants)",
                new
                {
                    member,
                    paused = runtime.PausedUntil is { } p ? StateJson.Instant(p) : null,
                    homework = runtime.HomeworkUntil is { } h ? StateJson.Instant(h) : null,
                    grants = StateJson.Grants(runtime.Grants),
                }, tx);

        foreach (var (member, device, minute) in state.Usage.Entries())
        {
            var key = (member, device, StateJson.ToUnixMinute(minute));
            if (_savedUsage.Add(key))
                db.Execute("INSERT OR IGNORE INTO usage_minutes (member_id, device_id, minute) VALUES (@member, @device, @minute)",
                    new { member, device, minute = key.Item3 }, tx);
        }

        var firstNew = _lastSavedEvent is null ? 0 : LastIndexOf(state.Events, _lastSavedEvent) + 1;
        foreach (var e in state.Events.Skip(firstNew))
            db.Execute("INSERT INTO events (ts, type, member_id, device_id, reason, url, app, remaining, detail, admin) VALUES (@ts, @type, @member, @device, @reason, @url, @app, @remaining, @detail, @admin)",
                new
                {
                    ts = StateJson.Instant(e.At), type = e.Type.ToString(), member = e.MemberId, device = e.DeviceId,
                    reason = e.Reason == ReasonCode.None ? null : e.Reason.ToWire(),
                    url = e.Url, app = e.App, remaining = e.RemainingMinutes, detail = e.Detail, admin = e.Admin,
                }, tx);
        if (state.Events.Count > 0) _lastSavedEvent = state.Events[^1];

        foreach (var r in state.Requests)
            db.Execute("""
                INSERT OR REPLACE INTO access_requests (id, member_id, device_id, kind, target, target_key, minutes, message, created_at, status, decided_at, created_rule_id)
                VALUES (@Id, @MemberId, @DeviceId, @kind, @Target, @TargetKey, @Minutes, @Message, @created, @status, @decided, @CreatedRuleId)
                """,
                new
                {
                    r.Id, r.MemberId, r.DeviceId, kind = r.Kind.ToString(), r.Target, r.TargetKey, r.Minutes, r.Message,
                    created = StateJson.Instant(r.CreatedAt), status = r.Status.ToString(),
                    decided = r.DecidedAt is { } d ? StateJson.Instant(d) : null, r.CreatedRuleId,
                }, tx);

        foreach (var (name, value) in new[] { ("version", state.Version), ("requests", state.RequestCounter), ("rules", state.RuleCounter) })
            db.Execute("INSERT OR REPLACE INTO counters (name, value) VALUES (@name, @value)", new { name, value }, tx);

        tx.Commit();
    }

    private static int LastIndexOf(IReadOnlyList<FamilyEvent> events, FamilyEvent target)
    {
        for (var i = events.Count - 1; i >= 0; i--)
            if (ReferenceEquals(events[i], target) || events[i] == target) return i;
        return -1;
    }

    /// <summary>Deletes usage and events past retention (MVP-PLAN §9). Call daily.</summary>
    public void Prune(DateTimeOffset now)
    {
        using var db = database.Open();
        db.Execute("DELETE FROM usage_minutes WHERE minute < @cutoff", new { cutoff = StateJson.ToUnixMinute(now - UsageRetention) });
        db.Execute("DELETE FROM events WHERE ts < @cutoff", new { cutoff = StateJson.Instant(now - EventRetention) });
    }
}

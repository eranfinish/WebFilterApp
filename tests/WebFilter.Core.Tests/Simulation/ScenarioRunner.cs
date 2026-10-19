using System.Text.Json.Nodes;
using WebFilter.Core.Decisions;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Time;
using WebFilter.Core.YouTube;
using static WebFilter.Core.Tests.Simulation.FixtureLoader;

namespace WebFilter.Core.Tests.Simulation;

/// <summary>
/// Level-1 runner: one hub and one agent per fixture device, in memory, with a simulated clock and network.
/// Sync is instant while an agent is reachable. Executes the steps of one scenario and collects every mismatch.
/// </summary>
internal sealed class ScenarioRunner
{
    private readonly string _scenarioId;
    private readonly FamilyConfig _config;
    private readonly HubCore _hub;
    private readonly Dictionary<string, AgentCore> _agents = [];
    private readonly Dictionary<string, string> _requestAliases = [];
    private readonly List<string> _failures = [];
    private readonly DateTimeOffset _start;
    private DateTimeOffset _now;

    private TimeZoneInfo Zone => _config.TimeZone;

    public ScenarioRunner(JsonObject fixture, JsonObject scenario)
    {
        _scenarioId = Str(scenario, "id");
        _config = FixtureLoader.Load(fixture, scenario["setup"]?.AsObject());
        _hub = new HubCore(_config);
        foreach (var device in _config.Devices) _agents[device.Id] = new AgentCore(device.Id);
        _start = _now = LocalTime.Parse(Str(scenario, "start"), DateTimeOffset.UnixEpoch, Zone);
        Sync();
    }

    public IReadOnlyList<string> Run(JsonArray steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i]!.AsObject();
            var label = $"step {i + 1} ({Str(step, "do")})";
            if (OptStr(step, "at") is { } at) _now = LocalTime.Parse(at, _now, Zone);
            try
            {
                Execute(step, label);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                _failures.Add($"{label}: threw {ex.GetType().Name}: {ex.Message}");
            }
            Sync();
        }
        return _failures;
    }

    private void Execute(JsonObject step, string label)
    {
        switch (Str(step, "do"))
        {
            case "navigate":
            {
                var agent = Agent(step);
                var account = OptStr(step, "account") is { } name ? agent.AccountNamed(name) : agent.AccountOf(Str(step, "member"));
                CheckDecision(label, Str(step, "url"), agent.DecideUrl(account, Str(step, "url"), _now), step["expect"]!);
                break;
            }
            case "checkUrls":
            {
                var agent = Agent(step);
                var account = agent.AccountOf(Str(step, "member"));
                var cases = step["cases"]!.AsArray();
                for (var c = 0; c < cases.Count; c++)
                    CheckDecision($"{label} case {c + 1}", Str(cases[c], "url"), agent.DecideUrl(account, Str(cases[c], "url"), _now), cases[c]!["expect"]!);
                break;
            }
            case "parseYouTube":
                foreach (var c in step["cases"]!.AsArray()) CheckYouTubeParse(label, c!);
                break;
            case "youtube":
            {
                var agent = Agent(step);
                var page = step["page"]!;
                var result = agent.DecideYouTube(agent.AccountOf(Str(step, "member")), new YouTubePage(Str(page, "url"), OptStr(page, "channelId"), OptStr(page, "handle")), _now);
                CheckDecision(label, Str(page, "url"), result, step["expect"]!);
                break;
            }
            case "app":
            {
                var agent = Agent(step);
                var app = step["app"]!;
                var identity = new AppIdentity(OptStr(app, "path"), OptStr(app, "package"), OptStr(app, "publisher"));
                CheckDecision(label, identity.Path ?? identity.Package ?? "", agent.DecideApp(agent.AccountOf(Str(step, "member")), identity, _now), step["expect"]!);
                break;
            }
            case "browse":
                Browse(step["sessions"]!.AsArray());
                break;
            case "setTime":
                _now = LocalTime.Parse(Str(step, "to"), _now, Zone);
                break;
            case "admin":
                Admin(step);
                break;
            case "expectTime":
                ExpectTime(label, step);
                break;
            case "expectEvents":
                ExpectEvents(label, step);
                break;
            case "network":
                _agents[Str(step, "device")].HubReachable = Str(step, "hub") == "online";
                break;
            case "service":
                var target = _agents[Str(step, "device")];
                target.ServiceUp = Str(step, "state") == "up";
                target.ProfileCachePresent = (OptStr(step, "profileCache") ?? "present") == "present";
                break;
            case "setSystemTimezone":
                break; // decisions use the family zone only
            case "requestAccess":
                RequestAccess(label, step);
                break;
            case "expectRequests":
                ExpectRequests(label, step);
                break;
            default:
                _failures.Add($"{label}: unknown step");
                break;
        }
    }

    // ---- steps ----------------------------------------------------------------------------------------------------

    private void Browse(JsonArray sessions)
    {
        var parsed = sessions.Select(s => (
            Member: Str(s, "member"),
            Agent: AgentFor(Str(s, "member"), OptStr(s, "device")),
            From: LocalTime.Parse(Str(s, "from"), _now, Zone),
            To: LocalTime.Parse(Str(s, "to"), _now, Zone),
            Active: s!["active"]?.GetValue<bool>() ?? true)).ToList();
        var end = parsed.Max(s => s.To);
        for (var t = parsed.Min(s => s.From); t <= end; t = t.AddSeconds(30))
        {
            foreach (var s in parsed.Where(s => s.From <= t && t < s.To))
                s.Agent.Heartbeat(s.Agent.AccountOf(s.Member), t, s.Active);
            Sync();
            _hub.Tick(t);
        }
        _now = end;
    }

    private void Admin(JsonObject step)
    {
        var action = Str(step, "action");
        var member = OptStr(step, "member");
        if ((OptStr(step, "scope") ?? "hub") == "local")
        {
            if (action != "pause") throw new NotSupportedException($"local '{action}' is not an agent action");
            var agent = _agents[Str(step, "device")];
            agent.LocalPause(member!, LocalTime.Parse(Str(step, "until"), _now, Zone), _now);
            return;
        }
        switch (action)
        {
            case "extendTime": _hub.ExtendTime(member!, step["minutes"]!.GetValue<int>(), _now); break;
            case "pause": _hub.Pause(member!, LocalTime.Parse(Str(step, "until"), _now, Zone), _now); break;
            case "resume": _hub.Resume(member!, _now); break;
            case "startHomework": _hub.StartHomework(member!, step["minutes"]!.GetValue<int>(), _now); break;
            case "stopHomework": _hub.StopHomework(member!, _now); break;
            case "addRule": _hub.AddRule(ParseRule(step["rule"], Zone), _now); break;
            case "removeRule": _hub.RemoveRule(Str(step, "ruleId"), _now); break;
            case "setMember":
                var patch = step["patch"]!.AsObject();
                _hub.SetMember(PatchMember(_config.Member(Str(patch, "id")), patch), _now);
                break;
            case "approveRequest":
                var duration = step["minutes"] is { } m && OptStr(step, "duration") is null
                    ? ApprovalDuration.For(m.GetValue<int>())
                    : OptStr(step, "duration") == "today" ? ApprovalDuration.Today : ApprovalDuration.Always;
                var scope = OptStr(step, "approveAs") == "page" ? ApproveScope.Page : ApproveScope.Host;
                _hub.Approve(_requestAliases[Str(step, "request")], scope, duration, step["minutes"]?.GetValue<int>(), _now);
                break;
            case "denyRequest": _hub.Deny(_requestAliases[Str(step, "request")], _now); break;
            default: throw new NotSupportedException($"admin action '{action}'");
        }
    }

    private void ExpectTime(string label, JsonObject step)
    {
        var member = Str(step, "member");
        var views = OptStr(step, "device") is { } device
            ? [(device, _agents[device].TimeStatus(member, _now))]
            : new[] { ("hub", _hub.TimeStatus(member, _now)) }.Concat(_agents.Values.Where(a => a.HubReachable).Select(a => (a.DeviceId, a.TimeStatus(member, _now)))).ToList();
        foreach (var (who, status) in views)
        {
            Compare(label, who, "state", Str(step, "state"), StateWire(status.State));
            if (step["usedMinutes"] is { } used) Compare(label, who, "usedMinutes", used.GetValue<int>(), status.UsedMinutes);
            if (step["limitMinutes"] is { } limit) Compare(label, who, "limitMinutes", limit.GetValue<int>(), status.LimitMinutes);
            if (step["remainingMinutes"] is { } remaining) Compare(label, who, "remainingMinutes", remaining.GetValue<int>(), status.RemainingMinutes);
            if (step.ContainsKey("windowEndsAt"))
            {
                var expected = OptStr(step, "windowEndsAt");
                var actual = status.WindowEndsAtMinute is { } w ? $"{w / 60:00}:{w % 60:00}" : null;
                Compare(label, who, "windowEndsAt", expected, actual);
            }
        }
    }

    private void ExpectEvents(string label, JsonObject step)
    {
        var type = ParseEventType(Str(step, "type"));
        var member = OptStr(step, "member");
        var since = OptStr(step, "since") is { } s ? LocalTime.Parse(s, _now, Zone) : _start;
        IEnumerable<FamilyEvent> source = OptStr(step, "device") is { } device ? _agents[device].LocalEvents : _hub.Events;
        var events = source.Where(e => e.Type == type && e.At >= since && (member is null || e.MemberId == member)).OrderBy(e => e.At).ToList();
        var expect = step["expect"]!;
        if (expect["count"] is { } count) Compare(label, "events", $"count of {Str(step, "type")}", count.GetValue<int>(), events.Count);
        var cursor = 0;
        foreach (var item in expect["items"]?.AsArray() ?? [])
        {
            var index = events.FindIndex(cursor, e => EventMatches(e, item!));
            if (index < 0) { _failures.Add($"{label}: no {Str(step, "type")} event matching {item!.ToJsonString()} (have: {string.Join("; ", events.Select(Describe))})"); continue; }
            cursor = index + 1;
        }
    }

    private void RequestAccess(string label, JsonObject step)
    {
        var memberId = Str(step, "member");
        var agent = AgentFor(memberId, OptStr(step, "device"));
        var kind = Str(step, "kind") switch { "url" => RequestKind.Url, "youtube" => RequestKind.YouTube, "app" => RequestKind.App, _ => RequestKind.Time };
        var target = OptStr(step, "target") ?? "";
        var draft = new AccessRequestDraft(memberId, agent.DeviceId, kind, target,
            HubCore.TargetKeyFor(kind, target, TimeCalculator.LocalDate(_now, Zone)), step["minutes"]?.GetValue<int>(), OptStr(step, "message"), _now);
        AccessRequest? hubRequest = null;
        var result = agent.RequestAccess(draft, d => _hub.CreateRequest(d, out hubRequest));
        var expect = step["expect"]!;
        Compare(label, agent.DeviceId, "result", Str(expect, "result"), ResultWire(result));
        var id = hubRequest?.Id ?? (result == RequestResult.Queued ? agent.QueuedRequests[^1].Id : null);
        if (OptStr(step, "as") is { } alias && id is not null) _requestAliases[alias] = id;
        if (OptStr(expect, "sameAs") is { } same) Compare(label, agent.DeviceId, "merged into", _requestAliases.GetValueOrDefault(same), id);
    }

    private void ExpectRequests(string label, JsonObject step)
    {
        IEnumerable<AccessRequest> source = OptStr(step, "device") is { } device ? _agents[device].QueuedRequests : _hub.Requests;
        var member = OptStr(step, "member");
        var status = OptStr(step, "status");
        var list = source.Where(r => (member is null || r.MemberId == member) && (status is null || StatusWire(r.Status) == status)).ToList();
        var expect = step["expect"]!;
        if (expect["count"] is { } count) Compare(label, "requests", "count", count.GetValue<int>(), list.Count);
        foreach (var item in expect["items"]?.AsArray() ?? [])
        {
            var alias = Str(item, "request");
            var request = _requestAliases.TryGetValue(alias, out var id) ? list.Find(r => r.Id == id) : null;
            if (request is null) { _failures.Add($"{label}: request '{alias}' not in list ({string.Join(", ", list.Select(r => r.Id))})"); continue; }
            if (OptStr(item, "status") is { } st) Compare(label, alias, "status", st, StatusWire(request.Status));
            if (OptStr(item, "kind") is { } kind) Compare(label, alias, "kind", kind, request.Kind.ToString().ToLowerInvariant());
            if (OptStr(item, "targetKey") is { } key) Compare(label, alias, "targetKey", key, request.TargetKey);
            if (OptStr(item, "message") is { } message) Compare(label, alias, "message", message, request.Message);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private void Sync()
    {
        foreach (var agent in _agents.Values.Where(a => a.HubReachable)) _hub.Ingest(agent.TakeUpload());
        var snapshot = _hub.Snapshot();
        foreach (var agent in _agents.Values.Where(a => a.HubReachable)) agent.Apply(snapshot);
    }

    private AgentCore Agent(JsonObject step) =>
        OptStr(step, "device") is { } device ? _agents[device] : AgentFor(Str(step, "member"), null);

    /// <summary>The given device, or the first fixture device on which the member has an account.</summary>
    private AgentCore AgentFor(string memberId, string? deviceId) =>
        deviceId is not null ? _agents[deviceId]
        : _agents[_config.Devices.First(d => d.Accounts.Any(a => a.MemberId == memberId)).Id];

    private void CheckDecision(string label, string subject, DecisionResult actual, JsonNode expect)
    {
        var decision = Str(expect, "decision");
        var actualDecision = actual.Decision switch { Decision.Allow => "allow", Decision.Block => "block", Decision.Redirect => "redirect", _ => "not_applicable" };
        var reason = OptStr(expect, "reason");
        var ruleId = OptStr(expect, "ruleId");
        if (decision != actualDecision || (reason is not null && reason != actual.Reason.ToWire()) || (ruleId is not null && ruleId != actual.RuleId))
            _failures.Add($"{label}: {subject} → expected {decision}/{reason}{(ruleId is null ? "" : "/" + ruleId)}, got {actualDecision}/{actual.Reason.ToWire()}{(actual.RuleId is null ? "" : "/" + actual.RuleId)}");
    }

    private void CheckYouTubeParse(string label, JsonNode c)
    {
        var url = Str(c, "url");
        var actual = YouTubeUrlParser.Parse(url);
        var expect = c["expect"]!;
        Compare(label, url, "kind", Str(expect, "kind"), KindWire(actual.Kind));
        if (OptStr(expect, "site") is { } site) Compare(label, url, "site", site, actual.Site?.ToString().ToLowerInvariant() is "nocookie" ? "nocookie" : actual.Site?.ToString().ToLowerInvariant());
        foreach (var (field, value) in new (string, string?)[]
                 {
                     ("channelId", actual.ChannelId), ("handle", actual.Handle), ("customName", actual.CustomName),
                     ("userName", actual.UserName), ("videoId", actual.VideoId), ("playlistId", actual.PlaylistId),
                 })
            if (OptStr(expect, field) is { } expected) Compare(label, url, field, expected, value);
    }

    private bool EventMatches(FamilyEvent e, JsonNode item) =>
        (OptStr(item, "at") is not { } at || (at.Contains('T') ? LocalTime.Full(e.At, Zone) == at[..16] : LocalTime.HhMm(e.At, Zone) == at[..5]))
        && (item["remainingMinutes"] is not { } r || e.RemainingMinutes == r.GetValue<int>())
        && (OptStr(item, "reason") is not { } reason || e.Reason.ToWire() == reason)
        && (OptStr(item, "url") is not { } url || e.Url == url)
        && (OptStr(item, "app") is not { } app || string.Equals(e.App, app, StringComparison.OrdinalIgnoreCase))
        && (OptStr(item, "device") is not { } device || e.DeviceId == device)
        && (OptStr(item, "detail") is not { } detail || e.Detail == detail);

    private string Describe(FamilyEvent e) =>
        $"{LocalTime.HhMm(e.At, Zone)} {e.MemberId} {e.DeviceId} {e.RemainingMinutes} {e.Reason.ToWire()} {e.App ?? e.Url} {e.Detail}".Replace("  ", " ");

    private void Compare<T>(string label, string who, string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            _failures.Add($"{label}: [{who}] {field} expected {expected?.ToString() ?? "null"}, got {actual?.ToString() ?? "null"}");
    }

    private static string StateWire(TimeState s) => s switch
    {
        TimeState.Ok => "ok", TimeState.Warn => "warn", TimeState.Expired => "expired", TimeState.OutsideWindow => "outside_window", _ => "unlimited",
    };

    private static string ResultWire(RequestResult r) => r switch
    {
        RequestResult.Created => "created", RequestResult.Merged => "merged", RequestResult.RateLimited => "rate_limited",
        RequestResult.Cooldown => "cooldown", RequestResult.Queued => "queued", _ => "unavailable",
    };

    private static string StatusWire(RequestStatus s) => s.ToString().ToLowerInvariant();

    private static string KindWire(YouTubeUrlKind k) => k == YouTubeUrlKind.NotYouTube ? "not_youtube" : k.ToString().ToLowerInvariant();

    private static EventType ParseEventType(string s) => s switch
    {
        "time_warning" => EventType.TimeWarning, "time_expired" => EventType.TimeExpired, "blocked" => EventType.Blocked,
        "app_blocked" => EventType.AppBlocked, "admin_change" => EventType.AdminChange, "access_request" => EventType.AccessRequest,
        "tamper" => EventType.Tamper, _ => EventType.Service,
    };
}

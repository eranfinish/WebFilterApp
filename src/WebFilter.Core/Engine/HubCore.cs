using WebFilter.Core.Dnr;
using WebFilter.Core.Model;
using WebFilter.Core.Time;
using WebFilter.Core.Urls;

namespace WebFilter.Core.Engine;

/// <summary>
/// The family's source of truth: settings, parent-set runtime state, usage merged from every agent, events and access
/// requests. Every change bumps <see cref="Version"/>; the host pushes <see cref="Snapshot"/> to reachable agents.
/// </summary>
public sealed class HubCore(FamilyConfig config)
{
    private readonly Dictionary<string, MemberRuntime> _runtime = [];
    private readonly Dictionary<string, TimeTrack> _timeTracks = [];
    private int _requestCounter;
    private int _ruleCounter;
    private PolicyCache? _policies;
    private int _policiesVersion;
    private HubSnapshot? _snapshot;

    private sealed record TimeTrack(DateOnly Date, int? Remaining, TimeState State);

    public FamilyConfig Config { get; private set; } = config;
    public int Version { get; private set; } = 1;
    public UsageLedger Usage { get; } = new();
    public List<FamilyEvent> Events { get; } = [];
    public List<AccessRequest> Requests { get; } = [];

    /// <summary>Raised for every event the hub records (its own and those agents upload), after it is in <see cref="Events"/>.</summary>
    public event Action<FamilyEvent>? EventRecorded;

    /// <summary>Raised for every new access request (created or delivered from an agent's queue), after it is in <see cref="Requests"/>.</summary>
    public event Action<AccessRequest>? RequestAccepted;

    /// <summary>Raised when a device reports accounts the settings did not have (<see cref="ReportAccounts"/>), after they are in <see cref="Config"/>.</summary>
    public event Action<NewAccounts>? AccountsAdded;

    /// <summary>
    /// Drops the oldest events, those before <paramref name="cutoff"/>, from memory (event retention, MVP-PLAN §9). Only a
    /// leading run is removed, so the order of what remains — which the store relies on to find what is new — is kept.
    /// </summary>
    public int PruneEvents(DateTimeOffset cutoff)
    {
        var count = 0;
        while (count < Events.Count && Events[count].At < cutoff) count++;
        Events.RemoveRange(0, count);
        return count;
    }

    public MemberRuntime RuntimeOf(string memberId) =>
        _runtime.TryGetValue(memberId, out var runtime) ? runtime : _runtime[memberId] = new MemberRuntime();

    /// <summary>The current snapshot; the same instance is returned until something changes.</summary>
    public HubSnapshot Snapshot() =>
        _snapshot is { } s && s.Version == Version
            ? s
            : _snapshot = new(Version, Config.Clone(), _runtime.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()), Usage.Clone());

    public PolicyView View()
    {
        if (_policies is null || _policiesVersion != Version) (_policies, _policiesVersion) = (new PolicyCache(Config), Version);
        return new PolicyView(Config, _runtime, Usage, new Dictionary<string, DateTimeOffset>(), _policies);
    }

    public TimeStatus TimeStatus(string memberId, DateTimeOffset now) => Decider.Time(View(), memberId, now);

    /// <summary>Everything that must survive a restart (the service persists it; see Storage in WebFilter.Service).</summary>
    public HubState Export() => new(Version, Config.Clone(), _runtime.ToDictionary(kv => kv.Key, kv => kv.Value.Clone()), Usage.Clone(),
        [.. Events], Requests.Select(r => r.Clone()).ToList(), _requestCounter, _ruleCounter);

    public static HubCore Restore(HubState state)
    {
        var hub = new HubCore(state.Config.Clone()) { Version = state.Version };
        foreach (var (member, runtime) in state.Runtime) hub._runtime[member] = runtime.Clone();
        hub.Usage.MergeFrom(state.Usage);
        hub.Events.AddRange(state.Events);
        hub.Requests.AddRange(state.Requests.Select(r => r.Clone()));
        hub._requestCounter = state.RequestCounter;
        hub._ruleCounter = state.RuleCounter;
        return hub;
    }

    public void Ingest(AgentUpload upload)
    {
        var before = Usage.Clone();
        Usage.MergeFrom(upload.Usage);
        foreach (var e in upload.Events) Record(e);
        foreach (var queued in upload.QueuedRequests) Accept(queued);
        if (!SameUsage(before)) Version++;
    }

    // ---- access requests ------------------------------------------------------------------------------------------

    public RequestResult CreateRequest(AccessRequestDraft draft, out AccessRequest? request)
    {
        request = Requests.FirstOrDefault(r => r.MemberId == draft.MemberId && r.TargetKey == draft.TargetKey && r.Status == RequestStatus.Pending);
        if (request is not null) return RequestResult.Merged;

        var lastDenial = Requests
            .Where(r => r.MemberId == draft.MemberId && r.TargetKey == draft.TargetKey && r.Status == RequestStatus.Denied)
            .Select(r => r.DecidedAt).Max();
        if (lastDenial is { } denied && draft.CreatedAt < denied + AccessRequestRules.Cooldown) return RequestResult.Cooldown;

        var recent = Requests.Count(r => r.MemberId == draft.MemberId && r.CreatedAt > draft.CreatedAt - AccessRequestRules.RateWindow);
        if (recent >= AccessRequestRules.MaxNewPerWindow) return RequestResult.RateLimited;

        request = draft.ToRequest($"ar-{++_requestCounter}");
        Accept(request);
        return RequestResult.Created;
    }

    public void Approve(string requestId, ApproveScope scope, ApprovalDuration duration, int? minutes, DateTimeOffset now)
    {
        var request = Request(requestId);
        var expires = duration.Kind switch
        {
            DurationKind.Minutes => now.AddMinutes(duration.Minutes),
            DurationKind.Today => TimeCalculator.NextLocalMidnight(now, Config.TimeZone),
            _ => (DateTimeOffset?)null,
        };
        switch (request.Kind)
        {
            case RequestKind.Url:
                var pattern = scope == ApproveScope.Page && UrlNormalizer.TryNormalize(request.Target, out var url)
                    ? DnrPatterns.FitPrefix(url.Host, url.Path)
                    : request.TargetKey;
                var rule = new Rule($"req-{++_ruleCounter}", request.MemberId, pattern, scope == ApproveScope.Page ? PatternType.Prefix : PatternType.Domain, RuleAction.Allow, ExpiresAt: expires);
                Config.Rules.Add(rule);
                request.CreatedRuleId = rule.Id;
                break;
            case RequestKind.YouTube:
                Config.YouTubeChannels.Add(new YouTubeChannel(request.Target, null, null, request.MemberId, RuleAction.Allow));
                break;
            case RequestKind.App:
                var isPath = request.Target.Contains('\\') || request.Target.Contains('/');
                var appRule = new AppRule($"req-{++_ruleCounter}", request.MemberId, isPath ? AppMatchType.Path : AppMatchType.Package, request.Target, RuleAction.Allow, ExpiresAt: expires);
                Config.AppRules.Add(appRule);
                request.CreatedRuleId = appRule.Id;
                break;
            case RequestKind.Time:
                ExtendTime(request.MemberId, minutes ?? request.Minutes ?? 15, now, raiseEvent: false);
                break;
        }
        Decide(request, RequestStatus.Approved, now, "approve_request");
    }

    public void Deny(string requestId, DateTimeOffset now) => Decide(Request(requestId), RequestStatus.Denied, now, "deny_request");

    public static string TargetKeyFor(RequestKind kind, string target, DateOnly localDate) => kind switch
    {
        RequestKind.Url => UrlNormalizer.TryNormalize(target, out var url) ? StripWww(url.Host) : target.ToLowerInvariant(),
        RequestKind.YouTube => target,
        RequestKind.App => target.ToLowerInvariant(),
        _ => $"time:{localDate:yyyy-MM-dd}",
    };

    // ---- admin actions --------------------------------------------------------------------------------------------

    /// <summary>
    /// Replaces all settings (setup wizard, member page, import). Runtime state, usage, events and requests are kept.
    /// The settings must pass <see cref="SettingsValidator"/>, and new or changed rules <see cref="RuleValidator"/>;
    /// rules already present are kept as they are.
    /// </summary>
    public void ReplaceSettings(FamilyConfig config, DateTimeOffset now)
    {
        SettingsValidator.EnsureValid(config);
        var existing = Config.Rules.ToHashSet();
        foreach (var rule in config.Rules.Where(r => !existing.Contains(r))) RuleValidator.EnsureValid(rule);
        Config = config;
        Changed(now, null, "replace_settings");
    }

    /// <summary>Adds a newly paired device (or updates its name and OS), keeping its known accounts.</summary>
    public void AddDevice(string id, string os, DateTimeOffset now)
    {
        var index = Config.Devices.FindIndex(d => d.Id == id);
        if (index >= 0) Config.Devices[index] = Config.Devices[index] with { Os = os };
        else Config.Devices.Add(new Device(id, os, "agent", []));
        Changed(now, null, "pair_device");
    }

    /// <summary>
    /// A paired device left by itself because it is being uninstalled; recorded for the activity log. Its entry stays, with
    /// its accounts and their links, so pairing the same computer again restores it as it was.
    /// </summary>
    public void DeviceUninstalled(string id, DateTimeOffset now)
    {
        Version++;
        Record(new FamilyEvent(EventType.AdminChange, now, null, id, Detail: "device_uninstalled"));
    }

    /// <summary>
    /// Records the accounts a device reports (tests/simulation/README.md §8): new ones are added unlinked (and not guests)
    /// until a parent answers for them; a known one keeps its link and the parents' guest answer, and takes the reported
    /// name and administrator flag — administrators are never filtered, so a flag left from before a parent made a
    /// child's account a standard one would leave the child unfiltered. Accounts missing from the report are kept. New
    /// accounts raise <see cref="AccountsAdded"/>.
    /// </summary>
    public void ReportAccounts(string deviceId, IEnumerable<DeviceAccount> seen, DateTimeOffset now)
    {
        var index = Config.Devices.FindIndex(d => d.Id == deviceId);
        if (index < 0) return;
        var accounts = Config.Devices[index].Accounts.ToList();
        var first = accounts.Count == 0;
        var added = new List<DeviceAccount>();
        var updated = false;
        foreach (var account in seen)
        {
            var known = accounts.FindIndex(a => string.Equals(a.AccountId, account.AccountId, StringComparison.OrdinalIgnoreCase));
            if (known < 0)
            {
                // Links and guest answers are the parents' to give, never the reporting computer's.
                accounts.Add(account with { MemberId = null, Guest = false });
                added.Add(accounts[^1]);
            }
            else if (accounts[known].Name != account.Name || accounts[known].IsAdmin != account.IsAdmin)
            {
                accounts[known] = accounts[known] with { Name = account.Name, IsAdmin = account.IsAdmin };
                updated = true;
            }
        }
        if (added.Count == 0 && !updated) return;
        Config.Devices[index] = Config.Devices[index] with { Accounts = accounts };
        Changed(now, null, added.Count > 0 ? "new_accounts" : "accounts_updated");
        if (added.Count > 0) AccountsAdded?.Invoke(new NewAccounts(deviceId, added, first));
    }

    public void AddRule(Rule rule, DateTimeOffset now)
    {
        RuleValidator.EnsureValid(rule);
        if (Config.Rules.Any(r => r.Id == rule.Id)) throw new ArgumentException($"A rule with id '{rule.Id}' already exists.", nameof(rule));
        Config.Rules.Add(rule);
        Changed(now, rule.MemberId, "add_rule");
    }

    public void RemoveRule(string ruleId, DateTimeOffset now)
    {
        var rule = Config.Rules.Find(r => r.Id == ruleId) ?? throw new KeyNotFoundException($"Unknown rule '{ruleId}'");
        Config.Rules.Remove(rule);
        Changed(now, rule.MemberId, "remove_rule");
    }

    public void SetMember(FamilyMember member, DateTimeOffset now)
    {
        var index = Config.Members.FindIndex(m => m.Id == member.Id);
        Config.Members[index] = member;
        Changed(now, member.Id, "set_member");
    }

    public void ExtendTime(string memberId, int minutes, DateTimeOffset now) => ExtendTime(memberId, minutes, now, raiseEvent: true);

    public void Pause(string memberId, DateTimeOffset until, DateTimeOffset now) { RuntimeOf(memberId).PausedUntil = until; Changed(now, memberId, "pause"); }

    public void Resume(string memberId, DateTimeOffset now) { RuntimeOf(memberId).PausedUntil = null; Changed(now, memberId, "resume"); }

    public void StartHomework(string memberId, int minutes, DateTimeOffset now) { RuntimeOf(memberId).HomeworkUntil = now.AddMinutes(minutes); Changed(now, memberId, "start_homework"); }

    public void StopHomework(string memberId, DateTimeOffset now) { RuntimeOf(memberId).HomeworkUntil = null; Changed(now, memberId, "stop_homework"); }

    // ---- time events ----------------------------------------------------------------------------------------------

    /// <summary>Raises time warnings (10/5/1 minutes left) and "time expired" when usage crosses them. Call on every time step.</summary>
    public void Tick(DateTimeOffset now)
    {
        foreach (var member in Config.Members.Where(m => m.IsRestricted))
        {
            var status = TimeStatus(member.Id, now);
            var today = TimeCalculator.LocalDate(now, Config.TimeZone);
            if (_timeTracks.TryGetValue(member.Id, out var track) && track.Date == today)
            {
                if (status.RemainingMinutes is { } remaining && track.Remaining is { } previous)
                    foreach (var threshold in new[] { 10, 5, 1 })
                        if (remaining <= threshold && previous > threshold)
                            Record(new FamilyEvent(EventType.TimeWarning, now, member.Id, null, RemainingMinutes: threshold));
                if (status.State == TimeState.Expired && track.State != TimeState.Expired)
                    Record(new FamilyEvent(EventType.TimeExpired, now, member.Id, null));
            }
            _timeTracks[member.Id] = new TimeTrack(today, status.RemainingMinutes, status.State);
        }
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private void ExtendTime(string memberId, int minutes, DateTimeOffset now, bool raiseEvent)
    {
        var runtime = RuntimeOf(memberId);
        runtime.Grants.Add(TimeCalculator.CreateExtension(MemberTimeRules.For(Config, memberId), Config.TimeZone, now, minutes, runtime.Grants));
        if (raiseEvent) Changed(now, memberId, "extend_time");
        else Version++;
    }

    private void Accept(AccessRequest request)
    {
        Requests.Add(request);
        Record(new FamilyEvent(EventType.AccessRequest, request.CreatedAt, request.MemberId, request.DeviceId, Url: request.Target, Detail: request.Kind.ToString().ToLowerInvariant()));
        Version++;
        RequestAccepted?.Invoke(request);
    }

    private void Record(FamilyEvent e)
    {
        Events.Add(e);
        EventRecorded?.Invoke(e);
    }

    private void Decide(AccessRequest request, RequestStatus status, DateTimeOffset now, string detail)
    {
        request.Status = status;
        request.DecidedAt = now;
        Changed(now, request.MemberId, detail);
    }

    private AccessRequest Request(string id) => Requests.Find(r => r.Id == id) ?? throw new KeyNotFoundException($"Unknown request '{id}'");

    /// <summary>Who makes the current change (the admin's Windows name), recorded on admin-change events (MVP-PLAN §10.2).</summary>
    public string? Actor { get; set; }

    private void Changed(DateTimeOffset now, string? memberId, string detail)
    {
        Version++;
        Record(new FamilyEvent(EventType.AdminChange, now, memberId, null, Detail: detail, Admin: Actor));
    }

    private bool SameUsage(UsageLedger before) =>
        Config.Members.All(m => Config.Devices.All(d => before.MinutesOn(m.Id, d.Id).Count == Usage.MinutesOn(m.Id, d.Id).Count));

    private static string StripWww(string host) => host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
}

/// <summary>Accounts a device reported that the settings did not have (<see cref="HubCore.AccountsAdded"/>), as stored: unlinked.</summary>
/// <param name="FirstReport">
/// The device had no accounts in the settings before: what it reports first after pairing, or the hub's own at first
/// setup — the accounts the computer already had, not ones just created.
/// </param>
public sealed record NewAccounts(string DeviceId, IReadOnlyList<DeviceAccount> Accounts, bool FirstReport);

/// <summary>A hub's persistent state.</summary>
public sealed record HubState(
    int Version,
    FamilyConfig Config,
    IReadOnlyDictionary<string, MemberRuntime> Runtime,
    UsageLedger Usage,
    IReadOnlyList<FamilyEvent> Events,
    IReadOnlyList<AccessRequest> Requests,
    int RequestCounter,
    int RuleCounter);

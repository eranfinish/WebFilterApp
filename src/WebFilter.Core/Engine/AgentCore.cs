using WebFilter.Core.Decisions;
using WebFilter.Core.Model;
using WebFilter.Core.Time;

namespace WebFilter.Core.Engine;

/// <summary>What an agent sends the hub: its usage, its events, and requests queued while the hub was unreachable.</summary>
public sealed record AgentUpload(string DeviceId, UsageLedger Usage, IReadOnlyList<FamilyEvent> Events, IReadOnlyList<AccessRequest> QueuedRequests);

/// <summary>
/// The per-device enforcement core. Decides locally from the last hub snapshot plus its own usage, so it keeps working
/// while the hub is unreachable. Transport-agnostic: the host (service or simulation) moves snapshots and uploads.
/// </summary>
public sealed class AgentCore(string deviceId)
{
    private HubSnapshot? _snapshot;
    private PolicyCache? _policies;
    private readonly UsageLedger _localUsage = new();
    private readonly Dictionary<string, DateTimeOffset> _localPauses = [];
    private readonly List<FamilyEvent> _outbox = [];
    private readonly List<FamilyEvent> _log = [];
    private readonly List<AccessRequest> _queued = [];
    private readonly Dictionary<string, DateTimeOffset> _decisionsSeen = [];
    private int _queuedCounter;

    public string DeviceId { get; } = deviceId;
    public bool ServiceUp { get; set; } = true;
    public bool ProfileCachePresent { get; set; } = true;
    public bool HubReachable { get; set; } = true;
    public int? SnapshotVersion => _snapshot?.Version;
    public IReadOnlyList<FamilyEvent> LocalEvents => _log;
    public IReadOnlyList<AccessRequest> QueuedRequests => _queued;

    /// <summary>The decisions in the last snapshot that carried them: request id → when it was decided.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> DecisionsSeen => _decisionsSeen;

    /// <summary>
    /// Takes a snapshot from the hub, and returns the decisions in it (<see cref="HubSnapshot.Decisions"/>) this device has
    /// not seen yet, of members with an account here: the host announces each to the member's browsers, once. A decision
    /// is remembered by its request id and decision time while the hub keeps sending it, so a request decided again is
    /// announced again. A snapshot without decisions (an older hub, the hub's own) announces nothing and forgets nothing.
    /// </summary>
    public IReadOnlyList<RequestDecision> Apply(HubSnapshot snapshot)
    {
        if (ReferenceEquals(snapshot, _snapshot)) return [];
        _snapshot = snapshot;
        _policies = new PolicyCache(snapshot.Config);
        if (snapshot.Decisions is not { } decisions) return [];

        var members = snapshot.Config.Devices.Find(d => d.Id == DeviceId)?.Accounts.Select(a => a.MemberId).OfType<string>().ToHashSet() ?? [];
        var fresh = decisions
            .Where(d => members.Contains(d.MemberId) && !(_decisionsSeen.TryGetValue(d.RequestId, out var seen) && seen == d.DecidedAt))
            .OrderBy(d => d.DecidedAt)
            .ToList();
        _decisionsSeen.Clear();
        foreach (var d in decisions) _decisionsSeen[d.RequestId] = d.DecidedAt;
        return fresh;
    }

    public Device Device => Snapshot.Config.Device(DeviceId);

    public DeviceAccount AccountOf(string memberId) =>
        Device.Accounts.FirstOrDefault(a => a.MemberId == memberId) ?? throw new KeyNotFoundException($"'{memberId}' has no account on {DeviceId}");

    public DeviceAccount AccountNamed(string name) =>
        Device.Accounts.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) ?? throw new KeyNotFoundException($"No account '{name}' on {DeviceId}");

    public PolicyView View()
    {
        var usage = Snapshot.Usage.Clone();
        usage.MergeFrom(_localUsage);
        return new PolicyView(Snapshot.Config, Snapshot.Runtime, usage, _localPauses, _policies);
    }

    /// <summary>The decision <see cref="DecideUrl"/> would make, without recording a block (the block page asking why).</summary>
    public DecisionResult ExplainUrl(DeviceAccount account, string url, DateTimeOffset now)
    {
        if (!ServiceUp) return Decider.FailClosed(account, Cache(account, now), url);
        return account.MemberId is null
            ? (account.IsAdmin ? Decider.Baseline(View(), null, url, now) : Decider.UnlinkedUrl(View(), url, now))
            : Decider.Url(View(), account.MemberId, url, now);
    }

    /// <summary>Decides and records a block — except for the parents' own accounts, whose browsing is never recorded (PROTECTION-SPEC §13).</summary>
    public DecisionResult DecideUrl(DeviceAccount account, string url, DateTimeOffset now)
    {
        var result = ExplainUrl(account, url, now);
        if (result.Decision == Decision.Block && ServiceUp && !IsParent(account))
            Raise(new FamilyEvent(EventType.Blocked, now, account.MemberId, DeviceId, result.Reason, Url: url));
        return result;
    }

    public DecisionResult DecideYouTube(DeviceAccount account, YouTubePage page, DateTimeOffset now)
    {
        if (!ServiceUp) return Decider.FailClosed(account, Cache(account, now), page.Url);
        // An administrator's YouTube is never filtered, like an unrestricted member's (Decider.YouTube).
        if (account.MemberId is null) return account.IsAdmin ? DecisionResult.Allow(ReasonCode.AllowedUnrestricted) : Decider.UnlinkedUrl(View(), page.Url, now);
        var result = Decider.YouTube(View(), account.MemberId, page, now);
        if (result.Decision == Decision.Block) Raise(new FamilyEvent(EventType.Blocked, now, account.MemberId, DeviceId, result.Reason, Url: page.Url));
        return result;
    }

    public DecisionResult DecideApp(DeviceAccount account, AppIdentity app, DateTimeOffset now)
    {
        if (account.MemberId is null) return account.IsAdmin ? DecisionResult.Allow(ReasonCode.AllowedUnrestricted) : DecisionResult.Block(ReasonCode.BlockedAppNetwork);
        var result = Decider.App(View(), account.MemberId, app, now);
        if (result.Decision == Decision.Block)
            Raise(new FamilyEvent(EventType.AppBlocked, now, account.MemberId, DeviceId, result.Reason, App: app.Path ?? app.Package ?? app.Publisher));
        return result;
    }

    /// <summary>A problem the browser extension reported (e.g. rules it had to drop), recorded as a service event.</summary>
    public void ReportProblem(DeviceAccount account, string detail, DateTimeOffset now) =>
        Raise(new FamilyEvent(EventType.Service, now, account.MemberId, DeviceId, Detail: detail));

    /// <summary>
    /// A block enforced by the extension itself (DNR or content script), reported for the parents' log. A parent's own
    /// block (the baseline protection, PROTECTION-SPEC §13) is not recorded. Returns whether it was.
    /// </summary>
    public bool ReportBlock(DeviceAccount account, string url, ReasonCode reason, DateTimeOffset now)
    {
        if (IsParent(account)) return false;
        Raise(new FamilyEvent(EventType.Blocked, now, account.MemberId, DeviceId, reason, Url: url));
        return true;
    }

    /// <summary>
    /// A search the member made (PROTECTION-SPEC §9), recorded as a <see cref="EventType.Search"/> event with detail
    /// "engine:query" — only when the account's member is restricted and has <see cref="FamilyMember.LogSearches"/>.
    /// Returns whether it was recorded.
    /// </summary>
    public bool ReportSearch(DeviceAccount account, string engine, string query, string? url, DateTimeOffset now)
    {
        if (!ServiceUp || account.MemberId is null || _snapshot is null) return false;
        var member = _snapshot.Config.Members.Find(m => m.Id == account.MemberId);
        if (member is not { IsRestricted: true, LogSearches: true }) return false;
        Raise(new FamilyEvent(EventType.Search, now, member.Id, DeviceId, Url: url, Detail: $"{engine}:{query}"));
        return true;
    }

    /// <summary>
    /// A teen opened a site the jump guard had blocked ("open anyway", PROTECTION-SPEC §7), recorded as an
    /// <see cref="EventType.Override"/> event. A parent's own "open anyway" (§13) is not recorded. Returns whether it was.
    /// </summary>
    public bool ReportOverride(DeviceAccount account, string url, ReasonCode reason, DateTimeOffset now)
    {
        if (IsParent(account)) return false;
        Raise(new FamilyEvent(EventType.Override, now, account.MemberId, DeviceId, reason, Url: url));
        return true;
    }

    /// <summary>
    /// One of the parents' own accounts (<see cref="BaselineProtection.AppliesTo"/>: an administrator linked to nobody, an
    /// unrestricted member's account), whose browsing is never recorded — however its baseline switches are set.
    /// </summary>
    public bool IsParent(DeviceAccount account) =>
        account.MemberId is null ? account.IsAdmin : _snapshot is { } snapshot && BaselineProtection.AppliesTo(snapshot.Config, account);

    /// <summary>
    /// Something on this computer worked against the filter and was undone or noticed (<see cref="TamperKinds"/>),
    /// recorded as a <see cref="EventType.Tamper"/> event; the hub turns it into a parent notification.
    /// <paramref name="accountId"/> (a Windows SID here) names the account it concerned, when there is one.
    /// </summary>
    public void ReportTamper(string? accountId, string detail, DateTimeOffset now)
    {
        var memberId = accountId is null ? null
            : _snapshot?.Config.Devices.FirstOrDefault(d => d.Id == DeviceId)?.Accounts
                .FirstOrDefault(a => string.Equals(a.AccountId, accountId, StringComparison.OrdinalIgnoreCase))?.MemberId;
        Raise(new FamilyEvent(EventType.Tamper, now, memberId, DeviceId, Detail: detail));
    }

    /// <summary>A browser heartbeat. The minute counts only when browsing is allowed at this instant.</summary>
    public void Heartbeat(DeviceAccount account, DateTimeOffset now, bool active)
    {
        if (!ServiceUp || !active || account.MemberId is null) return;
        if (Decider.CountsTowardsTime(View(), account.MemberId, now)) _localUsage.Mark(account.MemberId, DeviceId, now);
    }

    public TimeStatus TimeStatus(string memberId, DateTimeOffset now) => Decider.Time(View(), memberId, now);

    /// <summary>Emergency pause by an admin at this device; enforced here only and reported to the hub.</summary>
    public void LocalPause(string memberId, DateTimeOffset until, DateTimeOffset now)
    {
        _localPauses[memberId] = until;
        Raise(new FamilyEvent(EventType.AdminChange, now, memberId, DeviceId, Detail: "local_pause"));
    }

    /// <summary>Hands the request to the hub when reachable; otherwise queues it.</summary>
    public RequestResult RequestAccess(AccessRequestDraft draft, Func<AccessRequestDraft, RequestResult> sendToHub)
    {
        if (!ServiceUp) return RequestResult.Unavailable;
        if (HubReachable)
        {
            var sent = sendToHub(draft);
            if (sent != RequestResult.Queued) return sent;
            // The hub turned out to be unreachable: keep the request until it is.
        }
        if (_queued.Any(q => q.MemberId == draft.MemberId && q.TargetKey == draft.TargetKey)) return RequestResult.Merged;
        _queued.Add(draft.ToRequest($"{DeviceId}-q{++_queuedCounter}"));
        return RequestResult.Queued;
    }

    /// <summary>
    /// Everything that must survive a restart. Outbox and queued requests are included so nothing is lost offline, and the
    /// decisions seen so a restart does not announce them again.
    /// </summary>
    public AgentState Export() => new(DeviceId, _snapshot, _localUsage.Clone(), new Dictionary<string, DateTimeOffset>(_localPauses),
        [.. _outbox], _queued.Select(q => q.Clone()).ToList(), _queuedCounter, new Dictionary<string, DateTimeOffset>(_decisionsSeen));

    public static AgentCore Restore(AgentState state)
    {
        var agent = new AgentCore(state.DeviceId);
        // The decisions seen are restored as they were; the stored snapshot announces nothing.
        if (state.Snapshot is not null) agent.Apply(state.Snapshot with { Decisions = null });
        agent._localUsage.MergeFrom(state.LocalUsage);
        foreach (var (member, until) in state.LocalPauses) agent._localPauses[member] = until;
        agent._outbox.AddRange(state.Outbox);
        agent._queued.AddRange(state.QueuedRequests.Select(q => q.Clone()));
        agent._queuedCounter = state.QueuedCounter;
        foreach (var (request, decidedAt) in state.DecisionsSeen ?? new Dictionary<string, DateTimeOffset>()) agent._decisionsSeen[request] = decidedAt;
        return agent;
    }

    /// <summary>Puts back an upload that could not be delivered (usage needs nothing: the local ledger is never cleared).</summary>
    public void Requeue(AgentUpload upload)
    {
        _outbox.InsertRange(0, upload.Events);
        _queued.InsertRange(0, upload.QueuedRequests.Where(q => _queued.All(existing => existing.Id != q.Id)));
    }

    public AgentUpload TakeUpload()
    {
        var upload = new AgentUpload(DeviceId, _localUsage.Clone(), [.. _outbox], [.. _queued]);
        _outbox.Clear();
        _queued.Clear();
        return upload;
    }

    private HubSnapshot Snapshot => _snapshot ?? throw new InvalidOperationException($"{DeviceId} has not received a hub snapshot yet");

    private ProfileCacheEntry? Cache(DeviceAccount account, DateTimeOffset now) =>
        ProfileCachePresent && _snapshot is not null ? Decider.ProfileFor(_snapshot.Config, account, now) : null;

    private void Raise(FamilyEvent e)
    {
        // The local log is for diagnostics and tests only (the hub keeps the record): keep the recent part.
        if (_log.Count >= MaxLocalEvents) _log.RemoveRange(0, MaxLocalEvents / 2);
        _log.Add(e);
        _outbox.Add(e);
    }

    private const int MaxLocalEvents = 2000;
}

/// <summary>An agent's persistent state.</summary>
/// <param name="DecisionsSeen">
/// Request id → decision time of the decisions in the last snapshot that carried them (<see cref="AgentCore.Apply"/>), so
/// none is announced twice. Null: none seen yet.
/// </param>
public sealed record AgentState(
    string DeviceId,
    HubSnapshot? Snapshot,
    UsageLedger LocalUsage,
    IReadOnlyDictionary<string, DateTimeOffset> LocalPauses,
    IReadOnlyList<FamilyEvent> Outbox,
    IReadOnlyList<AccessRequest> QueuedRequests,
    int QueuedCounter,
    IReadOnlyDictionary<string, DateTimeOffset>? DecisionsSeen = null);

/// <summary>A request as the child submits it, before the hub assigns an id.</summary>
public sealed record AccessRequestDraft(
    string MemberId,
    string DeviceId,
    RequestKind Kind,
    string Target,
    string TargetKey,
    int? Minutes,
    string? Message,
    DateTimeOffset CreatedAt)
{
    public AccessRequest ToRequest(string id) => new()
    {
        Id = id, MemberId = MemberId, DeviceId = DeviceId, Kind = Kind, Target = Target, TargetKey = TargetKey,
        Minutes = Minutes, Message = Message, CreatedAt = CreatedAt,
    };
}

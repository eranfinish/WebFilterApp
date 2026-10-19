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
    private int _queuedCounter;

    public string DeviceId { get; } = deviceId;
    public bool ServiceUp { get; set; } = true;
    public bool ProfileCachePresent { get; set; } = true;
    public bool HubReachable { get; set; } = true;
    public int? SnapshotVersion => _snapshot?.Version;
    public IReadOnlyList<FamilyEvent> LocalEvents => _log;
    public IReadOnlyList<AccessRequest> QueuedRequests => _queued;

    public void Apply(HubSnapshot snapshot)
    {
        if (ReferenceEquals(snapshot, _snapshot)) return;
        _snapshot = snapshot;
        _policies = new PolicyCache(snapshot.Config);
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
            ? (account.IsAdmin ? DecisionResult.Allow(ReasonCode.AllowedUnrestricted) : Decider.UnlinkedUrl(View(), url, now))
            : Decider.Url(View(), account.MemberId, url, now);
    }

    public DecisionResult DecideUrl(DeviceAccount account, string url, DateTimeOffset now)
    {
        var result = ExplainUrl(account, url, now);
        if (result.Decision == Decision.Block && ServiceUp) Raise(new FamilyEvent(EventType.Blocked, now, account.MemberId, DeviceId, result.Reason, Url: url));
        return result;
    }

    public DecisionResult DecideYouTube(DeviceAccount account, YouTubePage page, DateTimeOffset now)
    {
        if (!ServiceUp) return Decider.FailClosed(account, Cache(account, now), page.Url);
        if (account.MemberId is null) return Decider.UnlinkedUrl(View(), page.Url, now);
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

    /// <summary>A block enforced by the extension itself (DNR or content script), reported for the parents' log.</summary>
    /// <summary>A problem the browser extension reported (e.g. rules it had to drop), recorded as a service event.</summary>
    public void ReportProblem(DeviceAccount account, string detail, DateTimeOffset now) =>
        Raise(new FamilyEvent(EventType.Service, now, account.MemberId, DeviceId, Detail: detail));

    public void ReportBlock(DeviceAccount account, string url, ReasonCode reason, DateTimeOffset now) =>
        Raise(new FamilyEvent(EventType.Blocked, now, account.MemberId, DeviceId, reason, Url: url));

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

    /// <summary>Everything that must survive a restart. Outbox and queued requests are included so nothing is lost offline.</summary>
    public AgentState Export() => new(DeviceId, _snapshot, _localUsage.Clone(), new Dictionary<string, DateTimeOffset>(_localPauses),
        [.. _outbox], _queued.Select(q => q.Clone()).ToList(), _queuedCounter);

    public static AgentCore Restore(AgentState state)
    {
        var agent = new AgentCore(state.DeviceId);
        if (state.Snapshot is not null) agent.Apply(state.Snapshot);
        agent._localUsage.MergeFrom(state.LocalUsage);
        foreach (var (member, until) in state.LocalPauses) agent._localPauses[member] = until;
        agent._outbox.AddRange(state.Outbox);
        agent._queued.AddRange(state.QueuedRequests.Select(q => q.Clone()));
        agent._queuedCounter = state.QueuedCounter;
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
        _log.Add(e);
        _outbox.Add(e);
    }
}

/// <summary>An agent's persistent state.</summary>
public sealed record AgentState(
    string DeviceId,
    HubSnapshot? Snapshot,
    UsageLedger LocalUsage,
    IReadOnlyDictionary<string, DateTimeOffset> LocalPauses,
    IReadOnlyList<FamilyEvent> Outbox,
    IReadOnlyList<AccessRequest> QueuedRequests,
    int QueuedCounter);

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

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

    public DecisionResult DecideUrl(DeviceAccount account, string url, DateTimeOffset now)
    {
        if (!ServiceUp) return Decider.FailClosed(account, Cache(account, now), url);
        var result = account.MemberId is null
            ? (account.IsAdmin ? DecisionResult.Allow(ReasonCode.AllowedUnrestricted) : Decider.UnlinkedUrl(View(), url, now))
            : Decider.Url(View(), account.MemberId, url, now);
        if (result.Decision == Decision.Block) Raise(new FamilyEvent(EventType.Blocked, now, account.MemberId, DeviceId, result.Reason, Url: url));
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
        if (HubReachable) return sendToHub(draft);
        if (_queued.Any(q => q.MemberId == draft.MemberId && q.TargetKey == draft.TargetKey)) return RequestResult.Merged;
        _queued.Add(draft.ToRequest($"{DeviceId}-q{++_queuedCounter}"));
        return RequestResult.Queued;
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

using Microsoft.Extensions.Options;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Decisions;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Time;
using WebFilter.Contracts.Admin;
using WebFilter.Service.Hub;
using WebFilter.Service.Mapping;
using WebFilter.Service.Storage;

namespace WebFilter.Service;

/// <summary>
/// Owns the engine cores of this device: the hub (when <see cref="ServiceOptions.Role"/> is "hub") and the local agent,
/// their persistence, the in-process sync between them, and the time tick. All access is serialized through one gate;
/// the pipe servers call in, and react to <see cref="SettingsChanged"/> and <see cref="Ticked"/>.
/// </summary>
public sealed class FamilyService : IDisposable
{
    private readonly object _gate = new();
    private readonly ServiceOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FamilyService> _log;
    private readonly HubStore? _hubStore;
    private readonly NotificationStore? _notifications;
    private readonly AgentStore _agentStore;
    private readonly ProfileCacheWriter _profiles;
    private readonly string _runningMarker;
    private readonly Dictionary<string, DateTimeOffset> _tamperReported = [];
    private HubCore? _hub;
    private AgentCore _agent;
    private Func<AccessRequestDraft, (RequestResult Result, string? Id)>? _remoteRequests;
    private bool _dirty;
    private int _lastVersion;
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;

    /// <summary>The same tamper (kind and account) is reported at most once in this time; repeats are only logged.</summary>
    public static readonly TimeSpan TamperThrottle = TimeSpan.FromHours(1);

    /// <summary>A paired computer that has not reported for this long becomes a "device" notification (PROTECTION-SPEC §10).</summary>
    public static readonly TimeSpan DeviceSilence = TimeSpan.FromDays(1);

    /// <summary>In <see cref="ServiceOptions.DataDirectory"/>: present while the service runs; found at start = the previous run did not stop cleanly.</summary>
    public const string RunningMarkerFileName = "running.marker";

    /// <summary>Raised (outside the gate) when the settings snapshot used for decisions changes.</summary>
    public event Action<int>? SettingsChanged;

    /// <summary>
    /// Raised (outside the gate, after <see cref="SettingsChanged"/>) when a parent approves or denies an access request:
    /// on the hub as the parent decides (<see cref="Decide"/>), on an agent once per decision the hub's snapshot brings
    /// (<see cref="ApplyRemoteSnapshot"/>), for a member with an account on this computer.
    /// </summary>
    public event Action<RequestDecision>? RequestDecided;

    /// <summary>Raised (outside the gate) after each time tick.</summary>
    public event Action<DateTimeOffset>? Ticked;

    public FamilyService(IOptions<ServiceOptions> options, TimeProvider time, ILogger<FamilyService> log)
    {
        _options = options.Value;
        _time = time;
        _log = log;
        Directory.CreateDirectory(_options.DataDirectory);
        _agentStore = new AgentStore(new SqliteDatabase(Path.Combine(_options.DataDirectory, "agent.db"), "agent"));
        if (IsHub)
        {
            var hubDb = new SqliteDatabase(Path.Combine(_options.DataDirectory, "hub.db"), "hub");
            _hubStore = new HubStore(hubDb);
            Devices = new DeviceStore(hubDb);
            _notifications = new NotificationStore(hubDb);
        }
        _agent = new AgentCore(_options.DeviceId);
        _profiles = new ProfileCacheWriter(_options.ProfileDirectory, log);
        _runningMarker = Path.Combine(_options.DataDirectory, RunningMarkerFileName);
    }

    public bool IsHub => _options.Role == "hub";

    /// <summary>Hub only: the CA, server certificate and paired-device registry (M6).</summary>
    public HubCertificates? Certificates { get; private set; }

    public DeviceStore? Devices { get; }

    public HubPairing Pairing { get; } = new();

    public ServiceOptions Options => _options;

    public string DeviceId => _options.DeviceId;

    public DateTimeOffset Now => _time.GetUtcNow();

    public void Start()
    {
        lock (_gate)
        {
            var unclean = File.Exists(_runningMarker);
            if (IsHub)
            {
                var state = _hubStore!.Load();
                _hub = state is not null ? HubCore.Restore(state) : new HubCore(Bootstrap());
                _hub.EventRecorded += OnHubEvent;
                _hub.RequestAccepted += OnHubRequest;
                _hub.AccountsAdded += OnHubAccounts;
                Certificates = HubCertificates.LoadOrCreate(Path.Combine(_options.DataDirectory, "certs"));
                _log.LogInformation("Hub {HubId} started at settings version {Version}", Certificates.HubId, _hub.Version);
            }

            _agent = _agentStore.Load(_options.DeviceId) is { } agentState ? AgentCore.Restore(agentState) : new AgentCore(_options.DeviceId);
            if (unclean)
            {
                // Killed, crashed or a power cut: the parents hear of it (PROTECTION-SPEC §10), through the hub like any event.
                _log.LogWarning("The service did not stop cleanly the previous time");
                _agent.ReportTamper(null, TamperKinds.UncleanStop, Now);
            }
            File.WriteAllText(_runningMarker, Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            SyncLocked();
            if (IsHub) ApplyRetentionLocked(Now);
            SaveLocked();
        }
    }

    /// <summary>
    /// Records that something worked against the filter on this computer and was undone (<see cref="TamperKinds"/>), for
    /// the activity log and a parent notification. The same kind for the same account is reported at most once per
    /// <see cref="TamperThrottle"/>, so a value something keeps changing does not flood the parents.
    /// </summary>
    /// <param name="accountId">The Windows account concerned (SID), or null for the computer as a whole.</param>
    public void ReportTamper(string? accountId, string detail) => WithAgent((agent, now) =>
    {
        var key = $"{detail}|{accountId}";
        if (_tamperReported.TryGetValue(key, out var last) && now - last < TamperThrottle) return 0;
        _tamperReported[key] = now;
        agent.ReportTamper(accountId, detail, now);
        return 0;
    });

    /// <summary>Hub only: the parent notifications recorded after <paramref name="since"/> (PROTECTION-SPEC §10), oldest first.</summary>
    public IReadOnlyList<Contracts.Notify.NotificationDto> NotificationsSince(DateTimeOffset? since) =>
        _notifications?.Since(since) ?? [];

    /// <summary>Runs <paramref name="action"/> on the local agent under the gate, then syncs and schedules a save.</summary>
    public T WithAgent<T>(Func<AgentCore, DateTimeOffset, T> action)
    {
        int? changed;
        T result;
        lock (_gate)
        {
            result = action(_agent, Now);
            changed = SyncLocked();
        }
        if (changed is { } v) SettingsChanged?.Invoke(v);
        return result;
    }

    /// <summary>
    /// Runs an admin action on the hub under the gate, then syncs and schedules a save. <paramref name="actor"/> (the
    /// admin's Windows name) is recorded on the admin-change events the action raises.
    /// </summary>
    public T WithHub<T>(Func<HubCore, DateTimeOffset, T> action, string? actor = null)
    {
        if (!IsHub) throw new InvalidOperationException("This device is not the hub.");
        int? changed;
        T result;
        lock (_gate)
        {
            _hub!.Actor = actor;
            try { result = action(_hub, Now); }
            finally { _hub.Actor = null; }
            changed = SyncLocked();
            // Admin changes are rare and must survive a crash: store them now, not on the next tick.
            SaveLocked();
        }
        if (changed is { } v) SettingsChanged?.Invoke(v);
        return result;
    }

    /// <summary>
    /// Approves or denies a request on the hub and notifies the requesting member's sessions on this computer; agents
    /// learn of it from the next snapshot (<see cref="SnapshotSince"/>), which the change pushes to them.
    /// </summary>
    public void Decide(string requestId, bool approve, ApproveScope scope, ApprovalDuration duration, int? minutes, string? actor = null)
    {
        var decision = WithHub((hub, now) =>
        {
            if (approve) hub.Approve(requestId, scope, duration, minutes, now);
            else hub.Deny(requestId, now);
            return hub.DecisionOf(hub.Requests.First(r => r.Id == requestId));
        }, actor);
        RequestDecided?.Invoke(decision);
    }

    /// <summary>Hands a request to the hub: in-process on the hub, over the network on an agent (Queued when unreachable).</summary>
    public RequestResult SendRequestToHub(AccessRequestDraft draft, out AccessRequest? request)
    {
        request = null;
        if (IsHub) return _hub!.CreateRequest(draft, out request);
        if (_remoteRequests is null) return RequestResult.Queued;
        var (result, id) = _remoteRequests(draft);
        if (id is not null) request = draft.ToRequest(id);
        return result;
    }

    // ---- agent role: the hub link drives these (M6) ---------------------------------------------------------------

    public void UseRemoteRequestSender(Func<AccessRequestDraft, (RequestResult, string?)> sender) => _remoteRequests = sender;

    public int? AgentSnapshotVersion { get { lock (_gate) return _agent.SnapshotVersion; } }

    /// <summary>
    /// Applies a snapshot from the hub. Each decision in it this computer has not announced yet (a request of a member with
    /// an account here, decided in the last 48 hours) raises <see cref="RequestDecided"/> once, after
    /// <see cref="SettingsChanged"/> as on the hub, so the browser installs the new rules before its block page reloads.
    /// What was announced is stored right after, so a restart does not announce it again.
    /// </summary>
    public void ApplyRemoteSnapshot(HubSnapshot snapshot)
    {
        var decided = WithAgent((agent, _) => agent.Apply(snapshot));
        if (decided.Count == 0) return;
        foreach (var decision in decided)
        {
            _log.LogInformation("Request {Request} of {Member} was {Status} on the hub; telling the browsers here", decision.RequestId, decision.MemberId,
                decision.Status.ToString().ToLowerInvariant());
            RequestDecided?.Invoke(decision);
        }
        try { Flush(); }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException) { _log.LogWarning(ex, "Could not store the announced decisions; the next tick will"); }
    }

    public AgentUpload TakeUploadForHub()
    {
        lock (_gate)
        {
            _dirty = true;
            return _agent.TakeUpload();
        }
    }

    public void RequeueUpload(AgentUpload upload)
    {
        lock (_gate) _agent.Requeue(upload);
    }

    public void SetHubReachable(bool reachable)
    {
        lock (_gate)
        {
            _agent.HubReachable = reachable;
            if (reachable) LastHubContactAt = Now;
        }
    }

    /// <summary>Agent role: the hub address this computer syncs with, once it is paired (set by <see cref="Hub.HubSyncService"/>).</summary>
    public string? HubAddress { get; private set; }

    /// <summary>Agent role: the last successful sync with the hub since the service started.</summary>
    public DateTimeOffset? LastHubContactAt { get; private set; }

    public void SetHubAddress(string address) => HubAddress = address;

    // ---- hub role: the hub API calls these (M6) -------------------------------------------------------------------

    /// <summary>Null when the agent already has <paramref name="since"/>. Carries the recent decisions (<see cref="HubCore.SnapshotForAgents"/>).</summary>
    public SnapshotDto? SnapshotSince(int since) => WithHub<SnapshotDto?>((hub, now) => hub.Version == since ? null : SyncMapper.ToDto(hub.SnapshotForAgents(now), now));

    public int IngestUpload(string deviceId, UploadDto dto) => WithHub((hub, now) =>
    {
        hub.Ingest(SyncMapper.FromDto(dto with { DeviceId = deviceId }, id => Devices!.FirstTimeSeen(deviceId, id, now)));
        if (dto.Accounts is { Count: > 0 } accounts)
            hub.ReportAccounts(deviceId, accounts.Select(a => new DeviceAccount(a.AccountId, a.Name, null, a.IsAdmin)), now);
        Devices!.Touch(deviceId, now);
        return hub.Version;
    });

    public CreateRequestResult CreateRequestFromAgent(string deviceId, RequestDto dto) => WithHub((hub, _) =>
    {
        var request = SyncMapper.FromDto(dto, deviceId);
        var draft = new AccessRequestDraft(request.MemberId, deviceId, request.Kind, request.Target, request.TargetKey, request.Minutes, request.Message, request.CreatedAt,
            request.Title, request.Handle, request.SearchQuery);
        var result = hub.CreateRequest(draft, out var created);
        return new CreateRequestResult(result switch
        {
            RequestResult.Created => "created", RequestResult.Merged => "merged", RequestResult.RateLimited => "rate_limited", _ => "cooldown",
        }, created?.Id);
    });

    public void RegisterPairedDevice(string deviceId, string name, string os, string thumbprint)
    {
        Devices!.Register(deviceId, name, os, thumbprint, Now);
        WithHub((hub, now) => { hub.AddDevice(deviceId, os, now); return 0; });
    }

    /// <summary>
    /// A paired device revokes itself (<see cref="UnregisterRequest"/>): revoked as a parent's revoke would, with the
    /// reason kept for the Devices screen, and an activity event. False when it was already revoked.
    /// </summary>
    public bool UnregisterDevice(string deviceId, string reason)
    {
        if (!Devices!.Revoke(deviceId, Now, reason)) return false;
        WithHub((hub, now) => { hub.DeviceUninstalled(deviceId, now); return 0; });
        return true;
    }

    /// <summary>
    /// Evaluates time warnings and flushes state; on the hub also notices paired computers that stopped reporting and,
    /// once a day, drops what is past retention. Called by <see cref="TickService"/>.
    /// </summary>
    public void Tick()
    {
        var now = Now;
        int? changed;
        lock (_gate)
        {
            _hub?.Tick(now);
            changed = SyncLocked();
            if (_hub is not null && now - _lastRetention >= TimeSpan.FromDays(1)) ApplyRetentionLocked(now);
            if (_dirty) SaveLocked();
        }
        if (IsHub) NotifySilentDevices(now);
        if (changed is { } v) SettingsChanged?.Invoke(v);
        Ticked?.Invoke(now);
    }

    public void Flush()
    {
        lock (_gate) SaveLocked();
    }

    /// <summary>A clean stop: state is stored and the running marker removed (so the next start is not an unclean one).</summary>
    public void Dispose()
    {
        Flush();
        StoppingCleanly();
    }

    /// <summary>
    /// A stop was asked for (Windows stops the service, or shuts down): the next start is a clean one even if Windows ends
    /// the process before the stop completes. Not called when a component fails (<see cref="FaultExit"/>): that is unclean.
    /// </summary>
    public void StoppingCleanly()
    {
        try { File.Delete(_runningMarker); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Could not remove {Marker}", _runningMarker); }
    }

    // ---- hub role: parent notifications (PROTECTION-SPEC §10) --------------------------------------------------------

    /// <summary>A tamper event from any computer (the hub's own included) becomes a "tamper" notification. Under the gate.</summary>
    private void OnHubEvent(FamilyEvent e)
    {
        if (e.Type != EventType.Tamper) return;
        Notify($"tamper:{SyncMapper.EventId(e)}", Contracts.Notify.NotificationKinds.Tamper, e.MemberId, e.DeviceId, e.Detail);
    }

    /// <summary>
    /// A new access request becomes a "request" notification, detail "kind:target" — for a YouTube channel whose name is
    /// known "youtube:channelId:name" (PROTECTION-SPEC §15). Under the gate.
    /// </summary>
    private void OnHubRequest(AccessRequest request)
    {
        var target = request.Kind switch
        {
            RequestKind.Url => request.TargetKey,
            RequestKind.Time => request.Minutes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            RequestKind.YouTube when YouTubeChannelLabel.Name(request.Title, request.Handle) is { } name => $"{request.Target}:{name}",
            _ => request.Target,
        };
        Notify($"request:{request.Id}:{request.CreatedAt.UtcTicks}", Contracts.Notify.NotificationKinds.Request, request.MemberId, request.DeviceId,
            $"{request.Kind.ToString().ToLowerInvariant()}:{target}");
    }

    /// <summary>
    /// A new standard account on any computer (the hub's own included) becomes an "account" notification, detail its
    /// Windows name: browsing there is blocked until a parent answers the admin app's new-account card. Not for
    /// administrators (never filtered), and not for a computer's first report — the accounts it already had when it was
    /// paired, or the hub's own at first setup, which the parent links while setting it up. One per account. Under the gate.
    /// </summary>
    private void OnHubAccounts(NewAccounts added)
    {
        if (added.FirstReport) return;
        var stored = _hub!.Config.Devices.Find(d => d.Id == added.DeviceId)?.Accounts ?? [];
        foreach (var account in added.Accounts)
        {
            // As the settings have it now: an account a parent has already answered for needs no notification.
            var current = stored.FirstOrDefault(a => string.Equals(a.AccountId, account.AccountId, StringComparison.OrdinalIgnoreCase)) ?? account;
            if (current.IsAdmin || current.MemberId is not null || current.Guest) continue;
            Notify($"account:{added.DeviceId}:{current.AccountId}", Contracts.Notify.NotificationKinds.Account, null, added.DeviceId, current.Name);
        }
    }

    /// <summary>Paired computers that have not reported for <see cref="DeviceSilence"/>: one notification per silence.</summary>
    private void NotifySilentDevices(DateTimeOffset now)
    {
        try
        {
            foreach (var device in Devices!.All())
            {
                if (device.RevokedAt is not null) continue;
                var last = device.LastSeenAt ?? device.PairedAt;
                if (now - last < DeviceSilence) continue;
                Notify($"device:{device.Id}:{last.UtcTicks}", Contracts.Notify.NotificationKinds.Device, null, device.Id,
                    device.LastSeenAt?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _log.LogWarning(ex, "Could not check for computers that stopped reporting");
        }
    }

    /// <summary>A failure to store a notification must not fail what raised it (an upload, a request).</summary>
    private void Notify(string id, string kind, string? memberId, string? deviceId, string? detail)
    {
        try
        {
            if (_notifications!.Add(id, kind, Now, memberId, deviceId, detail))
                _log.LogInformation("Notification {Kind} for {Member}/{Device}: {Detail}", kind, memberId, deviceId, detail);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _log.LogError(ex, "Could not record the {Kind} notification {Id}", kind, id);
        }
    }

    /// <summary>
    /// Drops events past retention (MVP-PLAN §9: 90 days) from memory and the database, with the event ids received from
    /// agents and old notifications. Search terms (PROTECTION-SPEC §9) are events too, so they go after 90 days as well.
    /// </summary>
    private void ApplyRetentionLocked(DateTimeOffset now)
    {
        _lastRetention = now;
        try
        {
            var dropped = _hub!.PruneEvents(now - HubStore.EventRetention);
            _hubStore!.Prune(now);
            Devices!.PruneSeenEvents(now - HubStore.EventRetention);
            _notifications!.Prune(now - NotificationStore.Retention);
            if (dropped > 0) _log.LogInformation("Dropped {Count} events past retention", dropped);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            _log.LogWarning(ex, "Could not apply retention; retrying tomorrow");
        }
    }

    private int? SyncLocked()
    {
        _dirty = true;
        if (_hub is not null)
        {
            _hub.Ingest(_agent.TakeUpload());
            _agent.Apply(_hub.Snapshot());
        }
        if (_agent.SnapshotVersion is not { } version || version == _lastVersion) return null;
        _lastVersion = version;
        _profiles.Write(_agent.View().Config, _options.DeviceId, version, Now);
        return version;
    }

    private void SaveLocked()
    {
        if (_hub is not null) _hubStore!.Save(_hub.Export(), Now);
        _agentStore.Save(_agent.Export());
        _dirty = false;
    }

    private FamilyConfig Bootstrap()
    {
        if (_options.BootstrapBundlePath is { } path)
        {
            _log.LogInformation("No stored settings; bootstrapping from {Path}", path);
            return BundleMapper.ToConfig(FamilyBundleJson.Deserialize(File.ReadAllText(path)));
        }
        _log.LogWarning("No stored settings and no bootstrap bundle; starting with an empty family");
        return new FamilyConfig
        {
            TimeZone = TimeZoneInfo.Local,
            Devices = [new Device(_options.DeviceId, "windows", "hub", [])],
        };
    }
}

/// <summary>Drives <see cref="FamilyService.Tick"/> on <see cref="ServiceOptions.TickInterval"/>.</summary>
public sealed class TickService(FamilyService family, IOptions<ServiceOptions> options, TimeProvider time, ILogger<TickService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.TickInterval, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { family.Tick(); }
            catch (Exception ex) { log.LogError(ex, "Tick failed"); }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        family.Flush();
    }
}

public static class ReasonCodeExtensions
{
    public static ReasonCode ParseOrNone(string? wire) =>
        wire is not null && ReasonCodes.AllWire.Contains(wire) ? ReasonCodes.Parse(wire) : ReasonCode.None;
}

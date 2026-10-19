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
    private readonly AgentStore _agentStore;
    private readonly ProfileCacheWriter _profiles;
    private HubCore? _hub;
    private AgentCore _agent;
    private Func<AccessRequestDraft, (RequestResult Result, string? Id)>? _remoteRequests;
    private bool _dirty;
    private int _lastVersion;

    /// <summary>Raised (outside the gate) when the settings snapshot used for decisions changes.</summary>
    public event Action<int>? SettingsChanged;

    /// <summary>Raised (outside the gate) when a parent approves or denies an access request.</summary>
    public event Action<AccessRequest>? RequestDecided;

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
        }
        _agent = new AgentCore(_options.DeviceId);
        _profiles = new ProfileCacheWriter(_options.ProfileDirectory, log);
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
            if (IsHub)
            {
                var state = _hubStore!.Load();
                _hub = state is not null ? HubCore.Restore(state) : new HubCore(Bootstrap());
                Certificates = HubCertificates.LoadOrCreate(Path.Combine(_options.DataDirectory, "certs"));
                _log.LogInformation("Hub {HubId} started at settings version {Version}", Certificates.HubId, _hub.Version);
            }

            _agent = _agentStore.Load(_options.DeviceId) is { } agentState ? AgentCore.Restore(agentState) : new AgentCore(_options.DeviceId);
            SyncLocked();
            SaveLocked();
        }
    }

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

    /// <summary>Approves or denies a request on the hub and notifies the requesting member's sessions.</summary>
    public void Decide(string requestId, bool approve, ApproveScope scope, ApprovalDuration duration, int? minutes, string? actor = null)
    {
        var request = WithHub((hub, now) =>
        {
            if (approve) hub.Approve(requestId, scope, duration, minutes, now);
            else hub.Deny(requestId, now);
            return hub.Requests.First(r => r.Id == requestId).Clone();
        }, actor);
        RequestDecided?.Invoke(request);
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

    public void ApplyRemoteSnapshot(HubSnapshot snapshot) => WithAgent((agent, _) => { agent.Apply(snapshot); return 0; });

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
        lock (_gate) _agent.HubReachable = reachable;
    }

    // ---- hub role: the hub API calls these (M6) -------------------------------------------------------------------

    /// <summary>Null when the agent already has <paramref name="since"/>.</summary>
    public SnapshotDto? SnapshotSince(int since) => WithHub<SnapshotDto?>((hub, now) => hub.Version == since ? null : SyncMapper.ToDto(hub.Snapshot(), now));

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
        var draft = new AccessRequestDraft(request.MemberId, deviceId, request.Kind, request.Target, request.TargetKey, request.Minutes, request.Message, request.CreatedAt);
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

    /// <summary>Evaluates time warnings and flushes state. Called by <see cref="TickService"/>.</summary>
    public void Tick()
    {
        var now = Now;
        int? changed;
        lock (_gate)
        {
            _hub?.Tick(now);
            changed = SyncLocked();
            if (_dirty) SaveLocked();
        }
        if (changed is { } v) SettingsChanged?.Invoke(v);
        Ticked?.Invoke(now);
    }

    public void Flush()
    {
        lock (_gate) SaveLocked();
    }

    public void Dispose() => Flush();

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

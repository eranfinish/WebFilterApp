using Microsoft.Extensions.Options;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Decisions;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Time;
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
        if (IsHub) _hubStore = new HubStore(new SqliteDatabase(Path.Combine(_options.DataDirectory, "hub.db"), "hub"));
        _agent = new AgentCore(_options.DeviceId);
        _profiles = new ProfileCacheWriter(_options.ProfileDirectory, log);
    }

    public bool IsHub => _options.Role == "hub";

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
                _log.LogInformation("Hub started at settings version {Version}", _hub.Version);
            }
            else
            {
                throw new NotSupportedException("The agent role syncs with a hub over the network (milestone M6).");
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

    /// <summary>Runs an admin action on the hub under the gate, then syncs and schedules a save.</summary>
    public T WithHub<T>(Func<HubCore, DateTimeOffset, T> action)
    {
        if (!IsHub) throw new InvalidOperationException("This device is not the hub.");
        int? changed;
        T result;
        lock (_gate)
        {
            result = action(_hub!, Now);
            changed = SyncLocked();
        }
        if (changed is { } v) SettingsChanged?.Invoke(v);
        return result;
    }

    /// <summary>Approves or denies a request on the hub and notifies the requesting member's sessions.</summary>
    public void Decide(string requestId, bool approve, ApproveScope scope, ApprovalDuration duration, int? minutes)
    {
        var request = WithHub((hub, now) =>
        {
            if (approve) hub.Approve(requestId, scope, duration, minutes, now);
            else hub.Deny(requestId, now);
            return hub.Requests.First(r => r.Id == requestId).Clone();
        });
        RequestDecided?.Invoke(request);
    }

    /// <summary>Hands a request to the hub (in-process when this device is the hub).</summary>
    public RequestResult SendRequestToHub(AccessRequestDraft draft, out AccessRequest? request)
    {
        request = null;
        if (!IsHub) return RequestResult.Queued;
        return _hub!.CreateRequest(draft, out request);
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
        if (_hub is null) return null;
        _hub.Ingest(_agent.TakeUpload());
        _agent.Apply(_hub.Snapshot());
        _dirty = true;
        if (_hub.Version == _lastVersion) return null;
        _lastVersion = _hub.Version;
        _profiles.Write(_hub.Config, _options.DeviceId, _lastVersion, Now);
        return _lastVersion;
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

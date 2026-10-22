using System.ComponentModel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Core.Engine;
using WebFilter.Service.Network;

namespace WebFilter.Service.Tests;

/// <summary>
/// M4: <see cref="WfpService"/> keeps WFP equal to the family settings — against a fake engine; the real one
/// (WebFilter.Wfp.Native.dll) runs only in the lab VM (tests/simulation/lab/Test-LabWfp.ps1). Passes are triggered the
/// way the running service triggers them: a settings change, or a tick of <see cref="FamilyService.Tick"/>.
/// </summary>
public sealed class WfpServiceTests : IDisposable
{
    /// <summary>pc-kids in the fixture: noa and yoav are restricted children, visitor is unlinked, dana is an admin, savta an unrestricted adult.</summary>
    private const string Noa = "S-1-5-21-2000-1001", Yoav = "S-1-5-21-2000-1002", Visitor = "S-1-5-21-2000-1005";
    private const string Minecraft = @"C:\Games\Minecraft\minecraft.exe";
    private static readonly DateTimeOffset InWindow = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00", System.Globalization.CultureInfo.InvariantCulture); // Monday
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-wfpsvc-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(InWindow);
    private readonly FakeEngine _engine = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ServiceOptions Settings(bool manageNetwork = true) => new()
    {
        Role = "agent", DeviceId = "pc-kids", ManageNetwork = manageNetwork,
        DataDirectory = Path.Combine(_dir, "data"), ProfileDirectory = Path.Combine(_dir, "profiles"),
    };

    private FamilyService Family(ServiceOptions options)
    {
        var family = new FamilyService(Options.Create(options), _clock, NullLogger<FamilyService>.Instance);
        family.Start();
        return family;
    }

    private WfpService Service(FamilyService family, ServiceOptions options) =>
        new(family, Options.Create(options), NullLogger<WfpService>.Instance, _engine);

    /// <summary>Waits for passes one by one; each result is true when the filters were applied.</summary>
    private sealed class Passes
    {
        private readonly SemaphoreSlim _done = new(0);
        private readonly Queue<bool> _results = new();

        public Passes(WfpService service) => service.Passed += applied =>
        {
            lock (_results) _results.Enqueue(applied);
            _done.Release();
        };

        public async Task<bool> Next()
        {
            Assert.True(await _done.WaitAsync(Wait), "no network pass");
            lock (_results) return _results.Dequeue();
        }
    }

    [Fact]
    public async Task Without_ManageNetwork_the_service_never_touches_wfp()
    {
        var options = Settings(manageNetwork: false);
        using var family = Family(options);
        family.ApplyRemoteSnapshot(new HubCore(PolicyTests.Fixture()).Snapshot());
        using var service = Service(family, options);

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!.WaitAsync(Wait);
        family.Tick();
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(0, _engine.Calls);
    }

    [Fact]
    public async Task An_agent_waits_for_its_first_settings_then_filters_the_restricted_accounts_only()
    {
        var options = Settings();
        using var family = Family(options);
        using var service = Service(family, options);
        var passes = new Passes(service);

        await service.StartAsync(CancellationToken.None);
        Assert.False(await passes.Next());
        Assert.Equal(0, _engine.Calls); // the filters from before stay as they are until the hub has spoken

        family.Tick();
        Assert.False(await passes.Next());
        Assert.Equal(0, _engine.Calls);

        family.ApplyRemoteSnapshot(new HubCore(PolicyTests.Fixture()).Snapshot());
        Assert.True(await passes.Next());
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(new[] { Noa, Yoav, Visitor }.Order(), _engine.Users.Keys.Order());
        Assert.Contains(Minecraft, _engine.Users[Yoav].AllowedPaths);
        Assert.False(_engine.Users[Yoav].TimeUp);
    }

    [Fact]
    public async Task The_tick_follows_the_time_window_without_any_settings_change()
    {
        var options = Settings();
        using var family = Family(options);
        family.ApplyRemoteSnapshot(new HubCore(PolicyTests.Fixture()).Snapshot());
        using var service = Service(family, options);
        var passes = new Passes(service);

        await service.StartAsync(CancellationToken.None);
        Assert.True(await passes.Next());
        Assert.Contains(Minecraft, _engine.Users[Yoav].AllowedPaths);

        // 22:00: yoav's window (14:00-21:30) has closed. Only the browsers and always-allowed programs stay.
        _clock.Now = new DateTimeOffset(InWindow.Year, InWindow.Month, InWindow.Day, 22, 0, 0, InWindow.Offset);
        family.Tick();
        Assert.True(await passes.Next());
        await service.StopAsync(CancellationToken.None);

        Assert.True(_engine.Users[Yoav].TimeUp);
        Assert.DoesNotContain(Minecraft, _engine.Users[Yoav].AllowedPaths);
        Assert.Empty(_engine.Users[Yoav].AllowedPackages);
    }

    [Fact]
    public async Task A_paused_member_loses_the_filters_as_soon_as_the_settings_arrive()
    {
        var options = Settings();
        using var family = Family(options);
        var hub = new HubCore(PolicyTests.Fixture());
        family.ApplyRemoteSnapshot(hub.Snapshot());
        using var service = Service(family, options);
        var passes = new Passes(service);

        await service.StartAsync(CancellationToken.None);
        Assert.True(await passes.Next());
        Assert.Contains(Yoav, _engine.Users.Keys);

        hub.Pause("yoav", InWindow.AddHours(1), InWindow);
        family.ApplyRemoteSnapshot(hub.Snapshot());
        Assert.True(await passes.Next());
        await service.StopAsync(CancellationToken.None);

        Assert.DoesNotContain(Yoav, _engine.Users.Keys);
        Assert.Contains(Noa, _engine.Users.Keys);
    }

    [Fact]
    public async Task A_failure_is_retried_on_the_next_tick_and_does_not_end_the_component()
    {
        var options = Settings();
        using var family = Family(options);
        family.ApplyRemoteSnapshot(new HubCore(PolicyTests.Fixture()).Snapshot());
        _engine.FailNext = true; // for example BFE not started yet at boot
        using var service = Service(family, options);
        var passes = new Passes(service);

        await service.StartAsync(CancellationToken.None);
        Assert.False(await passes.Next());
        Assert.False(service.ExecuteTask!.IsCompleted);

        family.Tick();
        Assert.True(await passes.Next());
        await service.StopAsync(CancellationToken.None);

        Assert.False(service.ExecuteTask.IsFaulted);
        Assert.Equal(new[] { Noa, Yoav, Visitor }.Order(), _engine.Users.Keys.Order());
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    private sealed class FakeEngine : IWfpEngine
    {
        private int _calls;

        public Dictionary<string, UserNetworkPolicy> Users { get; } = [];
        public int Calls => Volatile.Read(ref _calls);
        public bool FailNext { get; set; }

        public IReadOnlyCollection<string> FilteredUsers()
        {
            Interlocked.Increment(ref _calls);
            if (FailNext)
            {
                FailNext = false;
                throw new Win32Exception(1753, "WFP: could not list the filtered accounts"); // EPT_S_NOT_REGISTERED: BFE not running
            }
            return Users.Keys.ToList();
        }

        public IReadOnlyList<SkippedApp> SetUserPolicy(UserNetworkPolicy policy)
        {
            Interlocked.Increment(ref _calls);
            Users[policy.UserSid] = policy;
            return [];
        }

        public void RemoveUser(string userSid)
        {
            Interlocked.Increment(ref _calls);
            Users.Remove(userSid);
        }

        public void RemoveAll() => throw new InvalidOperationException("The service never removes everything.");
    }
}

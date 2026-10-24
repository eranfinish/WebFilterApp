using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Core.Engine;
using WebFilter.Service.Guard;
using WebFilter.Service.Network;

namespace WebFilter.Service.Tests;

/// <summary>
/// PROTECTION-SPEC §14: <see cref="ExtensionGuardService"/> with <see cref="WfpService"/>, against fake processes and a fake
/// WFP engine (the real ones run only in the lab). Passes are driven one by one at the test's clock, every 10 seconds as the
/// service runs them; the extension's sessions are opened on <see cref="ExtensionConnections"/> as the agent pipe opens them.
/// pc-kids in the fixture: noa and yoav are restricted children, visitor is unlinked, dana an administrator.
/// </summary>
public sealed class ExtensionGuardServiceTests : IAsyncLifetime
{
    private const string Noa = "S-1-5-21-2000-1001", Yoav = "S-1-5-21-2000-1002", Dana = "S-1-5-21-2000-1003";
    private const string ChromeId = "cccccccccccccccccccccccccccccccc", EdgeId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00", System.Globalization.CultureInfo.InvariantCulture); // Monday
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10), Step = TimeSpan.FromSeconds(10), Grace = TimeSpan.FromMinutes(3);
    private static readonly WindowsPrograms Machine = WindowsProgramPaths.Resolve();
    private static readonly string Chrome = Machine.MachineBrowsers.First(p => p.EndsWith(@"\chrome.exe", StringComparison.OrdinalIgnoreCase));
    private static readonly string Edge = Machine.MachineBrowsers.First(p => p.EndsWith(@"\msedge.exe", StringComparison.OrdinalIgnoreCase));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-guard-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(T0);
    private readonly FakeEngine _engine = new();
    private readonly FakePrograms _programs = new();
    private readonly ExtensionConnections _connections = new();
    private readonly BrowserBlocks _blocks = new();
    private FamilyService _family = null!;
    private ExtensionGuardService _guard = null!;
    private WfpService _wfp = null!;
    private Passes<ExtensionGuardResult?> _guardPasses = null!;
    private Passes<bool> _wfpPasses = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_guard is not null) await _guard.StopAsync(CancellationToken.None);
        if (_wfp is not null) await _wfp.StopAsync(CancellationToken.None);
        _guard?.Dispose();
        _wfp?.Dispose();
        _family?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ServiceOptions Settings(bool manageNetwork = true, bool guard = true) => new()
    {
        Role = "agent", DeviceId = "pc-kids", ManageNetwork = manageNetwork, ExtensionGuard = guard, ExtensionGrace = Grace,
        // Passes come from the test (Poke) and from hellos, never from the timer.
        ExtensionGuardInterval = Timeout.InfiniteTimeSpan,
        ChromeExtensionId = ChromeId, EdgeExtensionId = EdgeId,
        DataDirectory = Path.Combine(_dir, "data"), ProfileDirectory = Path.Combine(_dir, "profiles"),
    };

    /// <summary>Starts the family (with the fixture's settings unless <paramref name="settings"/> is false), WFP, then the guard; waits for their first passes.</summary>
    private async Task StartAsync(ServiceOptions? options = null, bool settings = true)
    {
        options ??= Settings();
        _family = new FamilyService(Options.Create(options), _clock, NullLogger<FamilyService>.Instance);
        _family.Start();
        if (settings) _family.ApplyRemoteSnapshot(new HubCore(PolicyTests.Fixture()).Snapshot());
        _wfp = new WfpService(_family, Options.Create(options), NullLogger<WfpService>.Instance, _engine, _blocks);
        _wfpPasses = new Passes<bool>(h => _wfp.Passed += h);
        _guard = new ExtensionGuardService(_family, _connections, _blocks, Options.Create(options), NullLogger<ExtensionGuardService>.Instance, _programs);
        _guardPasses = new Passes<ExtensionGuardResult?>(h => _guard.Passed += h);
        await _wfp.StartAsync(CancellationToken.None);
        if (options.ManageNetwork) await _wfpPasses.Next();
        await _guard.StartAsync(CancellationToken.None);
    }

    /// <summary>One guard pass at <paramref name="at"/>.</summary>
    private async Task<ExtensionGuardResult?> PassAt(DateTimeOffset at)
    {
        _clock.Now = at;
        _guard.Poke();
        return await _guardPasses.Next();
    }

    /// <summary>Guard passes every 10 seconds after <paramref name="from"/> up to <paramref name="to"/>; the last one's result.</summary>
    private async Task<ExtensionGuardResult?> PassesUntil(DateTimeOffset from, DateTimeOffset to)
    {
        ExtensionGuardResult? result = null;
        for (var t = from + Step; t <= to; t += Step) result = await PassAt(t);
        return result;
    }

    private IReadOnlyList<FamilyEvent> Tampers() =>
        _family.WithAgent((agent, _) => agent.LocalEvents.Where(e => e.Type == EventType.Tamper).ToList());

    [Fact]
    public async Task A_browser_without_the_extension_is_cut_off_after_the_window_and_gets_its_network_back_when_the_extension_connects()
    {
        _programs.Run(Noa, Chrome, 100);
        _programs.Run(Noa, Edge, 200);
        await StartAsync();
        Assert.Empty((await _guardPasses.Next())!.Blocked); // the first pass, at T0
        // Edge's extension (0.3.0) is connected; Chrome's (the 0.1.0 stub) never connects.
        var edge = _connections.Open(Noa, BrowserKind.Edge, EdgeId, T0);
        await _guardPasses.Next(); // a hello wakes the guard

        // The download window: everything allowed, nothing reported.
        var beforeEnd = T0 + Grace - Step;
        for (var t = T0 + Step; t <= beforeEnd; t += Step)
        {
            edge.Heard(t);
            Assert.Empty((await PassAt(t))!.Blocked);
        }
        Assert.Contains(Chrome, _engine.Users[Noa].AllowedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(Tampers());

        edge.Heard(T0 + Grace);
        var blocked = await PassAt(T0 + Grace);
        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Chrome)], blocked!.Blocked);
        Assert.True(await _wfpPasses.Next());
        Assert.DoesNotContain(_engine.Users[Noa].AllowedPaths, p => p.EndsWith("chrome.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Edge, _engine.Users[Noa].AllowedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Chrome, _engine.Users[Yoav].AllowedPaths, StringComparer.OrdinalIgnoreCase); // only noa's Chrome
        var tamper = Assert.Single(Tampers());
        Assert.Equal(("extension_missing:chrome", "noa", "pc-kids"), (tamper.Detail, tamper.MemberId, tamper.DeviceId));

        // The extension connects (an approved version arrived): restored at once, without waiting for the next poll.
        _clock.Now = T0 + Grace + TimeSpan.FromSeconds(5);
        _connections.Open(Noa.ToLowerInvariant(), BrowserKind.Chrome, ChromeId, _clock.Now);
        var restored = await _guardPasses.Next();
        Assert.Empty(restored!.Blocked);
        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Chrome)], restored.Restored);
        Assert.True(await _wfpPasses.Next());
        Assert.Contains(Chrome, _engine.Users[Noa].AllowedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Single(Tampers());
    }

    [Fact]
    public async Task Reopening_the_browser_opens_a_new_window_and_the_parents_hear_once_an_hour_per_browser()
    {
        _programs.Run(Noa, Chrome, 100);
        await StartAsync();
        await _guardPasses.Next();
        Assert.NotEmpty((await PassesUntil(T0, T0 + Grace))!.Blocked);
        Assert.True(await _wfpPasses.Next());
        Assert.Single(Tampers());

        // Chrome closed and opened again: allowed for a new window, then blocked again — not reported again within the hour.
        _programs.Clear();
        _programs.Run(Noa, Chrome, 300);
        var reopened = T0 + Grace + Step;
        Assert.Empty((await PassAt(reopened))!.Blocked);
        Assert.True(await _wfpPasses.Next());
        Assert.Contains(Chrome, _engine.Users[Noa].AllowedPaths, StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty((await PassesUntil(reopened, reopened + Grace))!.Started);
        Assert.Single(Tampers());

        // Edge is another browser: reported on its own.
        _programs.Run(Noa, Edge, 400);
        var edgeStart = reopened + Grace + Step;
        await PassAt(edgeStart);
        Assert.Contains(new AccountBrowser(Noa, BrowserKind.Edge), (await PassesUntil(edgeStart, edgeStart + Grace))!.Started);
        Assert.Equal(["extension_missing:chrome", "extension_missing:edge"], Tampers().Select(e => e.Detail));

        // After the hour, a new block of Chrome is reported again.
        _programs.Clear();
        var afterHour = T0 + TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1);
        await PassesUntil(edgeStart + Grace, afterHour);
        _programs.Run(Noa, Chrome, 500);
        await PassAt(afterHour + Step);
        await PassesUntil(afterHour + Step, afterHour + Step + Grace);
        Assert.Equal(["extension_missing:chrome", "extension_missing:edge", "extension_missing:chrome"], Tampers().Select(e => e.Detail));
    }

    [Fact]
    public async Task Administrators_and_browsers_that_do_not_run_are_never_blocked()
    {
        _programs.Run(Dana, Chrome, 100);
        _programs.Run(Dana, Edge, 101);
        await StartAsync();
        await _guardPasses.Next();

        var last = await PassesUntil(T0, T0 + Grace + TimeSpan.FromMinutes(2));

        Assert.Empty(last!.Blocked);
        Assert.Empty(_blocks.Current);
        Assert.Empty(Tampers());
        Assert.DoesNotContain(Dana, _engine.Users.Keys);
        Assert.Contains(Chrome, _engine.Users[Noa].AllowedPaths, StringComparer.OrdinalIgnoreCase);
        // The guard asked for the machine-wide browsers and the family's supported ones.
        Assert.Contains(Chrome, _programs.Asked, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(Edge, _programs.Asked, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_session_of_another_extension_does_not_count()
    {
        _programs.Run(Yoav, Chrome, 100);
        await StartAsync();
        await _guardPasses.Next();
        var other = _connections.Open(Yoav, BrowserKind.Chrome, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", T0);
        await _guardPasses.Next();

        for (var t = T0 + Step; t <= T0 + Grace; t += Step) other.Heard(t);
        Assert.Equal([new AccountBrowser(Yoav, BrowserKind.Chrome)], (await PassesUntil(T0, T0 + Grace))!.Blocked);
    }

    [Fact]
    public async Task An_agent_waits_for_its_first_settings_and_a_failure_is_retried()
    {
        _programs.Run(Noa, Chrome, 100);
        await StartAsync(settings: false);
        Assert.Null(await _guardPasses.Next());
        Assert.Equal(0, _programs.Calls);

        _family.ApplyRemoteSnapshot(new HubCore(PolicyTests.Fixture()).Snapshot()); // a settings change wakes the guard
        await _wfpPasses.Next();
        Assert.NotNull(await _guardPasses.Next());

        _programs.FailNext = true;
        Assert.Null(await PassAt(T0 + Step));
        Assert.False(_guard.ExecuteTask!.IsCompleted);
        Assert.NotNull(await PassAt(T0 + 2 * Step));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Without_ManageNetwork_or_with_the_guard_off_nothing_is_watched(bool manageNetwork, bool guard)
    {
        _programs.Run(Noa, Chrome, 100);
        await StartAsync(Settings(manageNetwork, guard));
        await _guard.ExecuteTask!.WaitAsync(Wait);
        _connections.Open(Noa, BrowserKind.Chrome, ChromeId, T0);

        Assert.Equal(0, _programs.Calls);
        Assert.Empty(_blocks.Current);
    }

    /// <summary>The service's container builds the guard, the agent pipe and WFP with one set of connections and blocks (Program.cs).</summary>
    [Fact]
    public async Task The_container_shares_the_connections_and_the_blocks()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(Settings()));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<FamilyService>();
        services.AddSingleton<ExtensionConnections>();
        services.AddSingleton<BrowserBlocks>();
        services.AddSingleton<IRunningPrograms>(_programs);
        services.AddHostedService<Pipes.AgentPipeServer>();
        services.AddHostedService<WfpService>();
        services.AddHostedService<ExtensionGuardService>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var hosted = provider.GetServices<IHostedService>().Select(s => s.GetType()).ToList();
        var family = provider.GetRequiredService<FamilyService>();
        family.Start();
        family.ApplyRemoteSnapshot(new HubCore(PolicyTests.Fixture()).Snapshot());
        var guard = provider.GetServices<IHostedService>().OfType<ExtensionGuardService>().Single();
        var passes = new Passes<ExtensionGuardResult?>(h => guard.Passed += h);
        await guard.StartAsync(CancellationToken.None);
        await passes.Next();
        provider.GetRequiredService<ExtensionConnections>().Open(Noa, BrowserKind.Chrome, ChromeId, T0); // the guard hears of it
        var woken = await passes.Next();
        await guard.StopAsync(CancellationToken.None);

        Assert.Equal([typeof(Pipes.AgentPipeServer), typeof(WfpService), typeof(ExtensionGuardService)], hosted);
        Assert.NotNull(woken);
        Assert.True(_programs.Calls > 0);
    }

    [Fact]
    public void The_running_programs_are_read_from_Windows_with_their_account_and_start()
    {
        using var self = Process.GetCurrentProcess();
        var path = Environment.ProcessPath!;

        var found = new WindowsRunningPrograms().Find([path.ToUpperInvariant(), @"C:\No\Such\Program\nothing.exe"]);

        var me = Assert.Single(found, p => p.ProcessId == Environment.ProcessId);
        Assert.Equal(path, me.Path, ignoreCase: true);
        Assert.Equal(WindowsIdentity.GetCurrent().User!.Value, me.UserSid);
        Assert.Equal(self.StartTime.ToUniversalTime(), me.StartedAt.UtcDateTime, TimeSpan.FromSeconds(1));
        Assert.Empty(new WindowsRunningPrograms().Find([@"C:\No\Such\Program\nothing.exe"]));
    }

    [Fact]
    public void A_closed_session_keeps_counting_until_it_is_stale_and_is_forgotten_later()
    {
        var connections = new ExtensionConnections();
        var hellos = 0;
        connections.Connected += () => hellos++;

        var session = connections.Open(Noa, BrowserKind.Edge, EdgeId + new string('x', 100), T0);
        session.Heard(T0 + TimeSpan.FromSeconds(30));
        session.Close();
        session.Heard(T0 + TimeSpan.FromSeconds(60)); // nothing arrives after the close

        var kept = Assert.Single(connections.Snapshot(T0 + TimeSpan.FromMinutes(5)));
        Assert.Equal((Noa, BrowserKind.Edge, T0 + TimeSpan.FromSeconds(30)), (kept.AccountId, kept.Browser, kept.LastHeard));
        Assert.Equal(64, kept.ExtensionId!.Length);
        Assert.Equal(1, hellos);
        Assert.Empty(connections.Snapshot(T0 + TimeSpan.FromSeconds(30) + ExtensionConnections.ClosedKept + TimeSpan.FromSeconds(1)));
    }

    /// <summary>Waits for the passes of a service one by one.</summary>
    private sealed class Passes<T>
    {
        private readonly SemaphoreSlim _done = new(0);
        private readonly Queue<T> _results = new();

        public Passes(Action<Action<T>> subscribe) => subscribe(result =>
        {
            lock (_results) _results.Enqueue(result);
            _done.Release();
        });

        public async Task<T> Next()
        {
            Assert.True(await _done.WaitAsync(Wait), "no pass");
            lock (_results) return _results.Dequeue();
        }
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    private sealed class FakePrograms : IRunningPrograms
    {
        private readonly List<RunningProgram> _running = [];
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public bool FailNext { get; set; }
        public IReadOnlyCollection<string> Asked { get; private set; } = [];

        public void Run(string sid, string path, int pid)
        {
            lock (_running) _running.Add(new RunningProgram(sid, path, pid, T0.AddHours(-1).AddSeconds(pid)));
        }

        public void Clear()
        {
            lock (_running) _running.Clear();
        }

        public IReadOnlyList<RunningProgram> Find(IReadOnlyCollection<string> paths)
        {
            Interlocked.Increment(ref _calls);
            Asked = paths.ToList();
            if (FailNext)
            {
                FailNext = false;
                throw new UnauthorizedAccessException("the process list could not be read");
            }
            lock (_running) return _running.Where(p => paths.Contains(p.Path, StringComparer.OrdinalIgnoreCase)).ToList();
        }
    }

    private sealed class FakeEngine : IWfpEngine
    {
        public Dictionary<string, UserNetworkPolicy> Users { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyCollection<string> FilteredUsers()
        {
            lock (Users) return Users.Keys.ToList();
        }

        public IReadOnlyList<SkippedApp> SetUserPolicy(UserNetworkPolicy policy)
        {
            lock (Users) Users[policy.UserSid] = policy;
            return [];
        }

        public void RemoveUser(string userSid)
        {
            lock (Users) Users.Remove(userSid);
        }

        public void RemoveAll() => throw new InvalidOperationException("The service never removes everything.");
    }
}

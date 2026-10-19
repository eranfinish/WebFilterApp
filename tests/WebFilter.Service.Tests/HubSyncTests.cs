using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Service.Hub;

namespace WebFilter.Service.Tests;

/// <summary>
/// M6 on one machine: a hub and an agent as two services talking HTTPS with mutual TLS over loopback —
/// pairing, snapshot sync, usage upload, change push, online and queued requests, wrong codes, revocation, and an
/// agent unregistering itself on uninstall (M7), with the hub on and off.
/// </summary>
public sealed class HubSyncTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-hub-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-11-02T15:00:00+02:00")); // Monday, inside yoav's window
    private readonly int _port = FreePort();
    private FamilyService _hub = null!;
    private HubServer _server = null!;
    private FamilyService _agent = null!;
    private HubSyncService _sync = null!;
    private string _agentData = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        var fixture = Path.Combine(RepoRoot(), "tests", "simulation", "fixtures", "family-base.json");
        _hub = new FamilyService(Options.Create(new ServiceOptions
        {
            Role = "hub", DeviceId = "pc-parents", DataDirectory = Path.Combine(_dir, "hub"), ProfileDirectory = Path.Combine(_dir, "hub-profiles"),
            HubBindAddress = "127.0.0.1", HubPort = _port, BootstrapBundlePath = fixture,
        }), _clock, NullLogger<FamilyService>.Instance);
        _hub.Start();
        await StartServerAsync();

        _agentData = Path.Combine(_dir, "agent");
        _agent = new FamilyService(Options.Create(new ServiceOptions
        {
            Role = "agent", DeviceId = "laptop-yoav", DataDirectory = _agentData, ProfileDirectory = Path.Combine(_dir, "agent-profiles"),
        }), _clock, NullLogger<FamilyService>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_sync is not null) await _sync.StopAsync(CancellationToken.None);
        await _server.StopAsync(CancellationToken.None);
        _agent.Dispose();
        _hub.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private async Task StartServerAsync()
    {
        _server = new HubServer(_hub, NullLogger<HubServer>.Instance);
        await _server.StartAsync(CancellationToken.None);
        await _server.Port.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private async Task PairAndSyncAsync()
    {
        await HubLink.PairAsync(_agentData, $"127.0.0.1:{_port}", _hub.Pairing.Create(_clock.GetUtcNow()), "laptop-yoav", "Yoav's laptop");
        _agent.Start();
        _sync = new HubSyncService(_agent, NullLogger<HubSyncService>.Instance);
        await _sync.StartAsync(CancellationToken.None);
        await _sync.FirstSync.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private int HubVersion => _hub.WithHub((hub, _) => hub.Version);

    [Fact]
    public async Task Pairing_registers_the_device_and_the_agent_receives_the_settings()
    {
        await PairAndSyncAsync();
        Assert.Contains(_hub.Devices!.All(), d => d.Id == "laptop-yoav" && d.RevokedAt is null);
        Assert.Equal(HubVersion, _agent.AgentSnapshotVersion);
        var member = _agent.WithAgent((agent, _) => agent.AccountOf("yoav").MemberId);
        Assert.Equal("yoav", member);
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_nothing_is_registered()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            HubLink.PairAsync(_agentData, $"127.0.0.1:{_port}", "AAAA-BBBB-CCCC", "laptop-yoav", "laptop"));
        Assert.DoesNotContain(_hub.Devices!.All(), d => d.Id == "laptop-yoav");
        Assert.Null(HubLink.Load(_agentData));
    }

    [Fact]
    public async Task Usage_reaches_the_hub_and_hub_changes_reach_the_agent_within_five_seconds()
    {
        await PairAndSyncAsync();
        _agent.WithAgent((agent, now) => { agent.Heartbeat(agent.AccountOf("yoav"), now, active: true); return 0; });
        _sync.Wake();
        await Eventually(() => _hub.WithHub((hub, _) => hub.Usage.MinutesOn("yoav", "laptop-yoav").Count) == 1, "usage uploaded");

        var watch = Stopwatch.StartNew();
        _hub.WithHub((hub, now) => { hub.AddRule(new Rule("r-sync", "yoav", "chess.example", PatternType.Domain, RuleAction.Block), now); return 0; });
        await Eventually(() => _agent.AgentSnapshotVersion == HubVersion, "settings pushed");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        var decision = _agent.WithAgent((agent, now) => agent.DecideUrl(agent.AccountOf("yoav"), "https://chess.example/", now));
        Assert.Equal("r-sync", decision.RuleId);
    }

    [Fact]
    public async Task Requests_go_straight_to_the_hub_when_reachable_and_are_queued_otherwise()
    {
        await PairAndSyncAsync();
        var online = Request("https://www.instagram.com/");
        Assert.Equal(RequestResult.Created, online);
        Assert.Single(_hub.WithHub((hub, _) => hub.Requests.ToList()));

        await _server.StopAsync(CancellationToken.None);
        await Eventually(() => Request("https://www.tiktok.com/") == RequestResult.Queued || _agent.WithAgent((a, _) => a.QueuedRequests.Count) > 0, "queued while the hub is down");

        await StartServerAsync();
        _sync.Wake();
        await Eventually(() => _hub.WithHub((hub, _) => hub.Requests.Count) == 2, "queued request delivered after the hub returned");
    }

    [Fact]
    public async Task A_revoked_device_is_shut_out_but_keeps_enforcing()
    {
        await PairAndSyncAsync();
        Assert.True(_hub.Devices!.Revoke("laptop-yoav", _clock.GetUtcNow()));
        _hub.WithHub((hub, now) => { hub.AddRule(new Rule("r-after", "yoav", "after.example", PatternType.Domain, RuleAction.Block), now); return 0; });
        _sync.Wake();
        await Eventually(() => Request("https://x.example/") == RequestResult.Queued, "hub unreachable for a revoked device");
        Assert.NotEqual(HubVersion, _agent.AgentSnapshotVersion);
        var stillFiltered = _agent.WithAgent((agent, now) => agent.DecideUrl(agent.AccountOf("yoav"), "https://www.instagram.com/", now));
        Assert.Equal(Core.Decisions.Decision.Block, stillFiltered.Decision);
    }

    [Fact]
    public async Task An_uninstalled_device_unregisters_itself_and_the_hub_lists_it_as_uninstalled_until_paired_again()
    {
        await PairAndSyncAsync();
        await _sync.StopAsync(CancellationToken.None); // uninstall stops the service first
        // A reason the hub does not know is refused and changes nothing.
        await Assert.ThrowsAsync<HttpRequestException>(() => HubLink.Load(_agentData)!.UnregisterAsync("bored", CancellationToken.None));
        Assert.Null(_hub.Devices!.All().Single(d => d.Id == "laptop-yoav").RevokedAt);

        Assert.Equal(UnregisterOutcome.Unregistered, await new HubUnregistration(_agentData).UnregisterAsync(CancellationToken.None));

        var device = _hub.Devices!.All().Single(d => d.Id == "laptop-yoav");
        Assert.NotNull(device.RevokedAt);
        Assert.Equal(Contracts.Hub.UnregisterRequest.Uninstall, device.RevokedReason);
        var hubAdmin = new Pipes.AdminPipeServer(_hub, Options.Create(_hub.Options), _clock, NullLogger<Pipes.AdminPipeServer>.Instance);
        var listed = Assert.IsType<Contracts.Admin.DevicesResult>(hubAdmin.Handle(new Contracts.Admin.GetDevices(), "parent")).Devices.Single(d => d.Id == "laptop-yoav");
        Assert.True(listed.Revoked);
        Assert.Equal("uninstall", listed.RevokedReason);
        Assert.Contains(_hub.WithHub((hub, _) => hub.Events.ToList()), e => e is { Type: EventType.AdminChange, Detail: "device_uninstalled", DeviceId: "laptop-yoav" });
        // Its entry in the settings (accounts and their links) stays for when the computer is paired again.
        Assert.Contains(_hub.WithHub((hub, _) => hub.Config.Devices.ToList()), d => d.Id == "laptop-yoav");

        // The hub no longer accepts it, so asking again says so.
        Assert.Equal(UnregisterOutcome.AlreadyRevoked, await new HubUnregistration(_agentData).UnregisterAsync(CancellationToken.None));

        // Installed again and paired with a new code: active again, without the reason.
        await HubLink.PairAsync(_agentData, $"127.0.0.1:{_port}", _hub.Pairing.Create(_clock.GetUtcNow()), "laptop-yoav", "Yoav's laptop");
        var again = _hub.Devices!.All().Single(d => d.Id == "laptop-yoav");
        Assert.Null(again.RevokedAt);
        Assert.Null(again.RevokedReason);
    }

    [Fact]
    public async Task A_device_a_parent_revoked_keeps_that_reason_when_it_is_uninstalled_later()
    {
        await PairAndSyncAsync();
        Assert.True(_hub.Devices!.Revoke("laptop-yoav", _clock.GetUtcNow()));

        Assert.Equal(UnregisterOutcome.AlreadyRevoked, await new HubUnregistration(_agentData).UnregisterAsync(CancellationToken.None));

        Assert.Null(_hub.Devices!.All().Single(d => d.Id == "laptop-yoav").RevokedReason);
        Assert.DoesNotContain(_hub.WithHub((hub, _) => hub.Events.ToList()), e => e.Detail == "device_uninstalled");
    }

    [Fact]
    public async Task Uninstall_cleanup_unregisters_over_the_real_link_then_deletes_the_pairing()
    {
        var root = Path.Combine(_dir, "attic");
        var data = Path.Combine(root, "data");
        await HubLink.PairAsync(data, $"127.0.0.1:{_port}", _hub.Pairing.Create(_clock.GetUtcNow()), "pc-attic", "ATTIC");
        var log = new StringWriter();

        var result = Uninstall(root, log);

        Assert.True(result.Complete);
        Assert.Equal(UnregisterOutcome.Unregistered, result.Hub);
        Assert.False(Directory.Exists(root));
        Assert.Equal("uninstall", _hub.Devices!.All().Single(d => d.Id == "pc-attic").RevokedReason);
    }

    [Fact]
    public async Task Uninstall_cleanup_with_the_hub_off_finishes_at_once_and_the_device_stays_listed()
    {
        var root = Path.Combine(_dir, "attic");
        var data = Path.Combine(root, "data");
        await HubLink.PairAsync(data, $"127.0.0.1:{_port}", _hub.Pairing.Create(_clock.GetUtcNow()), "pc-attic", "ATTIC");
        await _server.StopAsync(CancellationToken.None);
        var log = new StringWriter();

        var watch = Stopwatch.StartNew();
        var result = Uninstall(root, log);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        Assert.True(result.Complete);
        Assert.Equal(UnregisterOutcome.Failed, result.Hub);
        Assert.False(Directory.Exists(root));
        Assert.Contains("until a parent revokes it there", log.ToString(), StringComparison.Ordinal);
        Assert.Null(_hub.Devices!.All().Single(d => d.Id == "pc-attic").RevokedAt);
    }

    /// <summary><c>WebFilter.Service cleanup</c> on a computer whose data folder is <paramref name="root"/>, with the real hub link.</summary>
    private static Setup.MachineCleanup.Result Uninstall(string root, TextWriter log) =>
        new Setup.MachineCleanup(new MemoryPolicyStore(), new NoHives(), wfp: null, new HubUnregistration(Path.Combine(root, "data")), log)
            .Run(root, Path.Combine(root, "data", Policies.PolicyService.StateFileName));

    [Fact]
    public async Task On_an_agent_the_admin_pipe_gives_status_and_a_local_pause_and_refers_the_rest_to_the_hub()
    {
        await PairAndSyncAsync();
        var admin = new Pipes.AdminPipeServer(_agent, Options.Create(_agent.Options), _clock, NullLogger<Pipes.AdminPipeServer>.Instance);

        var status = Assert.IsType<Contracts.Admin.StatusResult>(admin.Handle(new Contracts.Admin.GetStatus(), "parent"));
        Assert.False(status.IsHub);
        Assert.Contains(status.Members, m => m.MemberId == "yoav");

        Assert.IsType<Contracts.Admin.AdminOk>(admin.Handle(new Contracts.Admin.Pause("yoav", _clock.GetUtcNow().AddMinutes(30)), "parent"));
        var paused = _agent.WithAgent((agent, now) => agent.DecideUrl(agent.AccountOf("yoav"), "https://www.instagram.com/", now));
        Assert.Equal(Core.Decisions.ReasonCode.AllowedPaused, paused.Reason);

        foreach (var hubOnly in new Contracts.Admin.AdminMessage[] { new Contracts.Admin.GetSettings(), new Contracts.Admin.GetDevices(), new Contracts.Admin.CreatePairingCode(), new Contracts.Admin.RevokeDevice("x"),
                     new Contracts.Admin.ScanAccounts(), new Contracts.Admin.ReplaceSettings(new Contracts.Hub.FamilyBundle { TimeZone = "Asia/Jerusalem" }, 1) })
            Assert.Equal("not_hub", Assert.IsType<Contracts.Admin.AdminError>(admin.Handle(hubOnly, "parent")).Code);
    }

    [Fact]
    public async Task A_new_pairing_code_replaces_the_previous_one()
    {
        var first = _hub.Pairing.Create(_clock.GetUtcNow());
        _hub.Pairing.Create(_clock.GetUtcNow());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => HubLink.PairAsync(_agentData, $"127.0.0.1:{_port}", first, "laptop-yoav", "laptop"));
    }

    [Fact]
    public void Admin_changes_are_stored_at_once_and_survive_a_crash()
    {
        _hub.WithHub((hub, now) => { hub.AddRule(new Rule("r-crash", "yoav", "crash.example", PatternType.Domain, RuleAction.Block), now); return 0; }, "parent");
        // No Flush or Dispose: a second service on the same data folder reads what the first one stored.
        using var afterCrash = new FamilyService(Options.Create(new ServiceOptions
        {
            Role = "hub", DeviceId = "pc-parents", DataDirectory = Path.Combine(_dir, "hub"), ProfileDirectory = Path.Combine(_dir, "hub-profiles-2"),
        }), _clock, NullLogger<FamilyService>.Instance);
        afterCrash.Start();
        Assert.Contains(afterCrash.WithHub((hub, _) => hub.Config.Rules.ToList()), r => r.Id == "r-crash");
    }

    private RequestResult Request(string url) => _agent.WithAgent((agent, now) =>
    {
        var draft = new AccessRequestDraft("yoav", "laptop-yoav", RequestKind.Url, url, HubCore.TargetKeyFor(RequestKind.Url, url, DateOnly.FromDateTime(now.DateTime)), null, null, now);
        return agent.RequestAccess(draft, d => _agent.SendRequestToHub(d, out _));
    });

    private static async Task Eventually(Func<bool> condition, string what, int timeoutMs = 20000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail($"Timed out waiting for: {what}");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx"))) return dir.FullName;
        throw new InvalidOperationException("WebFilter.slnx not found");
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>No policies were written, so no user hive is needed.</summary>
    private sealed class NoHives : Setup.IUserHives
    {
        public string? Load(string userSid) => null;
    }
}

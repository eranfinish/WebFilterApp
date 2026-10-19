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
/// pairing, snapshot sync, usage upload, change push, online and queued requests, wrong codes and revocation.
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

    private async Task PairAndSyncAsync(FakeLocalAccounts? accounts = null)
    {
        await HubLink.PairAsync(_agentData, $"127.0.0.1:{_port}", _hub.Pairing.Create(_clock.GetUtcNow()), "laptop-yoav", "Yoav's laptop");
        _agent.Start();
        _sync = new HubSyncService(_agent, NullLogger<HubSyncService>.Instance, accounts) { AccountsInterval = TimeSpan.Zero };
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
    public async Task On_an_agent_the_admin_pipe_gives_status_and_a_local_pause_and_refers_the_rest_to_the_hub()
    {
        await PairAndSyncAsync();
        var admin = new Pipes.AdminPipeServer(_agent, Options.Create(_agent.Options), _clock, NullLogger<Pipes.AdminPipeServer>.Instance);

        var status = Assert.IsType<Contracts.Admin.StatusResult>(admin.Handle(new Contracts.Admin.GetStatus(), "parent"));
        Assert.False(status.IsHub);
        Assert.Contains(status.Members, m => m.MemberId == "yoav");
        // "Managed by the hub ‹name›": its name from the settings, the address it syncs with, and when it was last heard.
        Assert.Equal("Asia/Jerusalem", status.TimeZone);
        Assert.Equal(("pc-parents", $"127.0.0.1:{_port}", true), (status.Hub!.Name, status.Hub.Address, status.Hub.Reachable));
        Assert.Equal(_clock.GetUtcNow(), status.Hub.LastContactAt);
        // The requirement checks are answered on an agent too, without the hub-only ones.
        var checks = Assert.IsType<Contracts.Admin.DiagnosticsResult>(admin.Handle(new Contracts.Admin.GetDiagnostics(), "parent")).Checks;
        Assert.Equal(["service", "browsers", "browser_policies", "extension"], checks.Select(c => c.Id));

        Assert.IsType<Contracts.Admin.AdminOk>(admin.Handle(new Contracts.Admin.Pause("yoav", _clock.GetUtcNow().AddMinutes(30)), "parent"));
        var paused = _agent.WithAgent((agent, now) => agent.DecideUrl(agent.AccountOf("yoav"), "https://www.instagram.com/", now));
        Assert.Equal(Core.Decisions.ReasonCode.AllowedPaused, paused.Reason);

        foreach (var hubOnly in new Contracts.Admin.AdminMessage[] { new Contracts.Admin.GetSettings(), new Contracts.Admin.GetDevices(), new Contracts.Admin.CreatePairingCode(), new Contracts.Admin.RevokeDevice("x"),
                     new Contracts.Admin.ScanAccounts(), new Contracts.Admin.ReplaceSettings(new Contracts.Hub.FamilyBundle { TimeZone = "Asia/Jerusalem" }, 1) })
            Assert.Equal("not_hub", Assert.IsType<Contracts.Admin.AdminError>(admin.Handle(hubOnly, "parent")).Code);
    }

    [Fact]
    public void An_agent_that_is_not_paired_yet_reports_status_without_members_or_hub()
    {
        _agent.Start();
        var admin = new Pipes.AdminPipeServer(_agent, Options.Create(_agent.Options), _clock, NullLogger<Pipes.AdminPipeServer>.Instance);

        var status = Assert.IsType<Contracts.Admin.StatusResult>(admin.Handle(new Contracts.Admin.GetStatus(), "parent"));

        Assert.Equal((false, 0, null), (status.IsHub, status.SettingsVersion, status.TimeZone));
        Assert.Empty(status.Members);
        Assert.Equal(new Contracts.Admin.HubConnection(null, null, false, null), status.Hub);
    }

    [Fact]
    public async Task An_agent_reports_its_accounts_and_an_accounts_admin_flag_follows_the_computer()
    {
        var accounts = new FakeLocalAccounts(
            new DeviceAccount("S-1-5-21-3000-1001", "yoav", null, IsAdmin: false),
            new DeviceAccount("S-1-5-21-3000-1002", "guest", null, IsAdmin: false));
        await PairAndSyncAsync(accounts);

        await Eventually(() => HubAccounts("laptop-yoav").Count == 2, "accounts reported");
        Assert.Equal("yoav", HubAccounts("laptop-yoav").Single(a => a.Name == "yoav").MemberId);
        Assert.Null(HubAccounts("laptop-yoav").Single(a => a.Name == "guest").MemberId);

        // The parent gave yoav's account administrator rights by mistake: the hub learns it (and keeps the link).
        accounts.Set(new DeviceAccount("S-1-5-21-3000-1001", "yoav", null, IsAdmin: true), new DeviceAccount("S-1-5-21-3000-1002", "guest", null, IsAdmin: false));
        _sync.Wake();
        await Eventually(() => HubAccounts("laptop-yoav").Single(a => a.Name == "yoav") is { IsAdmin: true, MemberId: "yoav" }, "admin flag updated");
        await Eventually(() => _agent.WithAgent((agent, _) => agent.AccountOf("yoav").IsAdmin), "the agent got the new flag");
    }

    [Fact]
    public async Task The_hub_reports_its_own_accounts()
    {
        var accounts = new FakeLocalAccounts(
            new DeviceAccount("S-1-5-21-1000-1001", "dana", null, IsAdmin: true),
            new DeviceAccount("S-1-5-21-1000-1002", "kids", null, IsAdmin: false));
        var own = new HubSyncService(_hub, NullLogger<HubSyncService>.Instance, accounts) { AccountsInterval = TimeSpan.FromMilliseconds(50) };
        await own.StartAsync(CancellationToken.None);
        try
        {
            await Eventually(() => HubAccounts("pc-parents").Any(a => a.Name == "kids"), "the hub's own accounts");
            Assert.Equal("dana", HubAccounts("pc-parents").Single(a => a.Name == "dana").MemberId);

            accounts.Add(new DeviceAccount("S-1-5-21-1000-1003", "guest", null, IsAdmin: false));
            await Eventually(() => HubAccounts("pc-parents").Any(a => a.Name == "guest"), "an account created later");
        }
        finally
        {
            await own.StopAsync(CancellationToken.None);
        }
    }

    private IReadOnlyList<DeviceAccount> HubAccounts(string deviceId) => _hub.WithHub((hub, _) => hub.Config.Device(deviceId).Accounts.ToList());

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
}

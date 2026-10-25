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
    private Pipes.AgentPipeServer? _agentPipe;
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
        if (_agentPipe is not null) await _agentPipe.StopAsync(CancellationToken.None);
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

    /// <summary>
    /// The lab case of 2026-10-24 (MVP-PLAN §8.1): the child asks from the agent computer's block page, a parent approves on
    /// the hub, and the agent's pipe tells the child's browser — after the rules changed, so the extension installs the
    /// allow rule before its block page reloads — once, though every later snapshot still carries the decision.
    /// </summary>
    [Fact]
    public async Task An_approval_on_the_hub_reaches_the_childs_browser_on_the_agent_once()
    {
        LinkYoavsLaptopAccountToThisUser();
        await PairAndSyncAsync();
        await using var browser = await ConnectBrowserAsync();
        Assert.Equal(Core.Decisions.Decision.Block, _agent.WithAgent((agent, now) => agent.DecideUrl(agent.AccountOf("yoav"), "https://www.instagram.com/", now)).Decision);

        var asked = await browser.RequestAsync<Contracts.Agent.RequestAccessResponse>(new Contracts.Agent.RequestAccessRequest("url", "https://www.instagram.com/", null, "for a school project"));
        Assert.Equal("created", asked.Result);
        Assert.Equal(_hub.WithHub((hub, _) => hub.Requests.Single().Id), asked.RequestId);

        _hub.Decide(asked.RequestId!, approve: true, ApproveScope.Host, ApprovalDuration.Always, null, "parent");
        var decidedVersion = HubVersion;

        var decided = await browser.WaitForAsync<Contracts.Agent.AccessDecidedMessage>(timeoutMs: 10000);
        Assert.NotNull(decided);
        Assert.Equal((asked.RequestId, "approved", (DateTimeOffset?)null), (decided.RequestId, decided.Status, decided.Until));
        var received = browser.Received.ToList();
        var rulesChanged = received.FindIndex(m => m is Contracts.Agent.RulesChangedMessage changed && changed.RulesVersion >= decidedVersion);
        Assert.InRange(rulesChanged, 0, received.IndexOf(decided) - 1);
        var rules = await browser.RequestAsync<Contracts.Agent.RulesResponse>(new Contracts.Agent.GetRulesRequest());
        Assert.Contains(rules.DynamicRules, r => r.GetProperty("action").GetProperty("type").GetString() == "allow" && r.ToString().Contains("\"instagram.com\"", StringComparison.Ordinal));
        Assert.Equal(Core.Decisions.ReasonCode.AllowedRule, _agent.WithAgent((agent, now) => agent.DecideUrl(agent.AccountOf("yoav"), "https://www.instagram.com/", now)).Reason);

        // Later snapshots carry the decision for 48 hours: the browser does not hear of it again.
        _hub.WithHub((hub, now) => { hub.AddRule(new Rule("r-later", "yoav", "chess.example", PatternType.Domain, RuleAction.Block), now); return 0; });
        await Eventually(() => _agent.AgentSnapshotVersion == HubVersion, "a later snapshot");
        await Task.Delay(300);
        Assert.Equal(1, browser.CountOf<Contracts.Agent.AccessDecidedMessage>());
    }

    /// <summary>
    /// A request queued while the hub was off keeps the agent's id at the hub, so the decision comes back under the id the
    /// browser was given — also to a second ask for the same site, merged into it.
    /// </summary>
    [Fact]
    public async Task A_decision_on_a_request_queued_while_the_hub_was_off_reaches_the_browser_under_the_queued_id()
    {
        LinkYoavsLaptopAccountToThisUser();
        await PairAndSyncAsync();
        await using var browser = await ConnectBrowserAsync();
        // No sync while the hub is off, so the queue is never on its way when the second ask comes; the last one failed.
        await _sync.StopAsync(CancellationToken.None);
        await _server.StopAsync(CancellationToken.None);
        _agent.SetHubReachable(false);

        var queued = await browser.RequestAsync<Contracts.Agent.RequestAccessResponse>(new Contracts.Agent.RequestAccessRequest("url", "https://www.instagram.com/", null, null));
        var merged = await browser.RequestAsync<Contracts.Agent.RequestAccessResponse>(new Contracts.Agent.RequestAccessRequest("url", "https://www.instagram.com/reels/", null, null));
        Assert.Equal(("queued", "laptop-yoav-q1"), (queued.Result, queued.RequestId));
        Assert.Equal(("merged", "laptop-yoav-q1"), (merged.Result, merged.RequestId));

        await StartServerAsync();
        _sync = new HubSyncService(_agent, NullLogger<HubSyncService>.Instance);
        await _sync.StartAsync(CancellationToken.None);
        await Eventually(() => _hub.WithHub((hub, _) => hub.Requests.Count) == 1, "the queued request reached the hub");
        Assert.Equal("laptop-yoav-q1", _hub.WithHub((hub, _) => hub.Requests.Single().Id));

        _hub.Decide("laptop-yoav-q1", approve: false, ApproveScope.Host, ApprovalDuration.Always, null, "parent");

        var decided = await browser.WaitForAsync<Contracts.Agent.AccessDecidedMessage>(timeoutMs: 10000);
        Assert.Equal(("laptop-yoav-q1", "denied"), (decided?.RequestId, decided?.Status));
    }

    /// <summary>
    /// PROTECTION-SPEC §15 across computers: a YouTube request from the agent's browser brings the channel's names and the
    /// search to the hub (the parent's notification names the channel), and the approved channel comes back to the agent
    /// with them — also for a request queued while the hub was off.
    /// </summary>
    [Fact]
    public async Task A_youtube_request_from_an_agent_names_its_channel_at_the_hub_and_the_approval_brings_the_names_back()
    {
        LinkYoavsLaptopAccountToThisUser();
        await PairAndSyncAsync();
        await using var browser = await ConnectBrowserAsync();
        const string natGeo = "UCnatgeoKids000000000004", sesame = "UCsesame0000000000000002";

        var asked = await browser.RequestAsync<Contracts.Agent.RequestAccessResponse>(
            new Contracts.Agent.RequestAccessRequest("youtube", natGeo, null, null, "NatGeo Kids", "@NatGeoKids", "animals"));
        Assert.Equal("created", asked.Result);
        var atHub = _hub.WithHub((hub, _) => hub.Requests.Single());
        Assert.Equal(("NatGeo Kids", "@natgeokids", "animals"), (atHub.Title, atHub.Handle, atHub.SearchQuery));
        Assert.Contains(_hub.NotificationsSince(null), n => n.Detail == $"youtube:{natGeo}:NatGeo Kids (@natgeokids)");

        _hub.Decide(asked.RequestId!, approve: true, ApproveScope.Host, ApprovalDuration.Always, null, "parent");
        await Eventually(() => _agent.AgentSnapshotVersion == HubVersion, "the approval's snapshot");
        Assert.Equal(new YouTubeChannel(natGeo, "@natgeokids", "NatGeo Kids", "yoav", RuleAction.Allow),
            _agent.WithAgent((agent, _) => agent.View().Config.YouTubeChannels.Single(c => c.ChannelId == natGeo)));

        // Queued while the hub is off: the names travel in the upload.
        await _sync.StopAsync(CancellationToken.None);
        await _server.StopAsync(CancellationToken.None);
        _agent.SetHubReachable(false);
        var queued = await browser.RequestAsync<Contracts.Agent.RequestAccessResponse>(
            new Contracts.Agent.RequestAccessRequest("youtube", sesame, null, null, "Sesame", "@sesame", null));
        Assert.Equal("queued", queued.Result);
        await StartServerAsync();
        _sync = new HubSyncService(_agent, NullLogger<HubSyncService>.Instance);
        await _sync.StartAsync(CancellationToken.None);
        await Eventually(() => _hub.WithHub((hub, _) => hub.Requests.Count) == 2, "the queued request reached the hub");
        var delivered = _hub.WithHub((hub, _) => hub.Requests.Single(r => r.Target == sesame));
        Assert.Equal(("Sesame", "@sesame", (string?)null), (delivered.Title, delivered.Handle, delivered.SearchQuery));
    }

    /// <summary>The settings give yoav's account on laptop-yoav the SID of whoever runs the tests, who then plays the child's browser.</summary>
    private void LinkYoavsLaptopAccountToThisUser() => _hub.WithHub((hub, now) =>
    {
        var config = hub.Config.Clone();
        var index = config.Devices.FindIndex(d => d.Id == "laptop-yoav");
        config.Devices[index] = config.Devices[index] with { Accounts = [new DeviceAccount(System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value, "yoav", "yoav", IsAdmin: false)] };
        hub.ReplaceSettings(config, now);
        return 0;
    }, "parent");

    /// <summary>The agent's pipe, with the test process as the trusted native host, and a browser that said hello.</summary>
    private async Task<AgentClient> ConnectBrowserAsync()
    {
        var options = new ServiceOptions { AgentPipeName = $"WebFilter.Test.Agent.{Guid.NewGuid().ToString("N")[..8]}", TrustedClientPaths = [Environment.ProcessPath!] };
        _agentPipe = new Pipes.AgentPipeServer(_agent, Options.Create(options), NullLogger<Pipes.AgentPipeServer>.Instance);
        await _agentPipe.StartAsync(CancellationToken.None);
        var browser = await AgentClient.ConnectAsync(options.AgentPipeName);
        await browser.RequestAsync<Contracts.Agent.HelloResponse>(new Contracts.Agent.HelloRequest("0.3.0", "edge", "abcdefghijklmnopabcdefghijklmnop"));
        return browser;
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
    public async Task The_hub_switches_its_public_networks_to_private_on_request_and_an_agent_refers_it_to_the_hub()
    {
        await PairAndSyncAsync();
        var networks = new FakeNetworks(["Network 2"]);
        var hubAdmin = new Pipes.AdminPipeServer(_hub, Options.Create(_hub.Options), _clock, NullLogger<Pipes.AdminPipeServer>.Instance, networks: networks);
        var ok = Assert.IsType<Contracts.Admin.AdminOk>(hubAdmin.Handle(new Contracts.Admin.SetNetworksPrivate(), "parent"));
        Assert.Equal("Network 2", ok.Detail);
        Assert.Empty(networks.Public);

        var agentAdmin = new Pipes.AdminPipeServer(_agent, Options.Create(_agent.Options), _clock, NullLogger<Pipes.AdminPipeServer>.Instance, networks: networks);
        Assert.Equal("not_hub", Assert.IsType<Contracts.Admin.AdminError>(agentAdmin.Handle(new Contracts.Admin.SetNetworksPrivate(), "parent")).Code);
    }

    private sealed class FakeNetworks(IEnumerable<string> publicOnes) : Network.INetworkProfiles
    {
        public List<string> Public { get; } = [.. publicOnes];

        public IReadOnlyList<string> MakePublicNetworksPrivate()
        {
            var changed = Public.ToList();
            Public.Clear();
            return changed;
        }
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
        // "Managed by the hub ‹name›": its name from the settings, the address it syncs with, and when it was last heard.
        Assert.Equal("Asia/Jerusalem", status.TimeZone);
        Assert.Equal(("pc-parents", $"127.0.0.1:{_port}", true), (status.Hub!.Name, status.Hub.Address, status.Hub.Reachable));
        Assert.Equal(_clock.GetUtcNow(), status.Hub.LastContactAt);
        // The requirement checks are answered on an agent too, without the hub-only ones.
        var checks = Assert.IsType<Contracts.Admin.DiagnosticsResult>(admin.Handle(new Contracts.Admin.GetDiagnostics(), "parent")).Checks;
        Assert.Equal(["service", "browsers", "browser_policies", "extension", "onedrive"], checks.Select(c => c.Id));

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
        // The settings had laptop-yoav with yoav's account before it was paired (as after a reinstall), so "guest" is an
        // account that appeared since: the parents hear of it (PROTECTION-SPEC §10).
        Assert.Equal([("laptop-yoav", "guest")], AccountNotifications());

        // The parent gave yoav's account administrator rights by mistake: the hub learns it (and keeps the link).
        accounts.Set(new DeviceAccount("S-1-5-21-3000-1001", "yoav", null, IsAdmin: true), new DeviceAccount("S-1-5-21-3000-1002", "guest", null, IsAdmin: false));
        _sync.Wake();
        await Eventually(() => HubAccounts("laptop-yoav").Single(a => a.Name == "yoav") is { IsAdmin: true, MemberId: "yoav" }, "admin flag updated");
        await Eventually(() => _agent.WithAgent((agent, _) => agent.AccountOf("yoav").IsAdmin), "the agent got the new flag");
        Assert.Single(AccountNotifications());
    }

    /// <summary>
    /// The parents keep an account as a guest from the admin app (ReplaceSettings, as the app saves): the answer survives
    /// the computer reporting its accounts again and reaches the agent; linking the account later drops it.
    /// </summary>
    [Fact]
    public async Task A_guest_answer_survives_a_new_account_report_and_the_sync_and_linking_drops_it()
    {
        var accounts = new FakeLocalAccounts(
            new DeviceAccount("S-1-5-21-3000-1001", "yoav", null, IsAdmin: false),
            new DeviceAccount("S-1-5-21-3000-1002", "guest", null, IsAdmin: false));
        await PairAndSyncAsync(accounts);
        await Eventually(() => HubAccounts("laptop-yoav").Count == 2, "accounts reported");
        var admin = new Pipes.AdminPipeServer(_hub, Options.Create(_hub.Options), _clock, NullLogger<Pipes.AdminPipeServer>.Instance);

        Replace(admin, a => a.Name == "guest" ? a with { Guest = true } : a);
        Assert.True(HubAccounts("laptop-yoav").Single(a => a.Name == "guest").Guest);

        // The computer reports again (the account was renamed, yoav's became an administrator): the answer stays.
        accounts.Set(new DeviceAccount("S-1-5-21-3000-1001", "yoav", null, IsAdmin: true), new DeviceAccount("S-1-5-21-3000-1002", "visitor", null, IsAdmin: false));
        _sync.Wake();
        await Eventually(() => HubAccounts("laptop-yoav").Single(a => a.AccountId == "S-1-5-21-3000-1002").Name == "visitor", "renamed account reported");
        Assert.Equal(new DeviceAccount("S-1-5-21-3000-1002", "visitor", null, false, Guest: true), HubAccounts("laptop-yoav").Single(a => a.Name == "visitor"));
        await Eventually(() => _agent.AgentSnapshotVersion == HubVersion, "settings synced");
        Assert.True(_agent.WithAgent((agent, _) => agent.AccountNamed("visitor").Guest));
        var blocked = _agent.WithAgent((agent, now) => agent.DecideUrl(agent.AccountNamed("visitor"), "https://www.wikipedia.org/", now));
        Assert.Equal(Core.Decisions.ReasonCode.BlockedNotInAllowlist, blocked.Reason);

        // Linked to a member later: no longer a guest, even though the bundle sent back still says so.
        Replace(admin, a => a.Name == "visitor" ? a with { Member = "yoav" } : a);
        var linked = HubAccounts("laptop-yoav").Single(a => a.Name == "visitor");
        Assert.Equal(("yoav", false), (linked.MemberId, linked.Guest));
    }

    /// <summary>Reads the settings, changes laptop-yoav's accounts and replaces them with <c>ifVersion</c>, as the admin app does.</summary>
    private static void Replace(Pipes.AdminPipeServer admin, Func<Contracts.Hub.AccountDto, Contracts.Hub.AccountDto> change)
    {
        var settings = Assert.IsType<Contracts.Admin.SettingsResult>(admin.Handle(new Contracts.Admin.GetSettings(), "parent")).Settings;
        var changed = settings with
        {
            Devices = [.. settings.Devices.Select(d => d.Id != "laptop-yoav" ? d : d with { Accounts = [.. d.Accounts.Select(change)] })],
        };
        Assert.IsType<Contracts.Admin.AdminOk>(admin.Handle(new Contracts.Admin.ReplaceSettings(changed, settings.Version), "parent"));
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
            // Recorded with the account, under the same lock: the notifier's next poll has it.
            Assert.Contains(("pc-parents", "guest"), AccountNotifications());
        }
        finally
        {
            await own.StopAsync(CancellationToken.None);
        }
    }

    private IReadOnlyList<DeviceAccount> HubAccounts(string deviceId) => _hub.WithHub((hub, _) => hub.Config.Device(deviceId).Accounts.ToList());

    private List<(string? DeviceId, string? Name)> AccountNotifications() =>
        [.. _hub.NotificationsSince(null).Where(n => n.Kind == Contracts.Notify.NotificationKinds.Account).Select(n => (n.DeviceId, n.Detail))];

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

    /// <summary>
    /// PROTECTION-SPEC §6 and §10 end to end: a search and an override on an agent computer, uploaded over HTTPS, are in
    /// the hub's activity (GetEvents) with their details; a tamper there becomes a notification on the hub.
    /// </summary>
    [Fact]
    public async Task Searches_overrides_and_tamper_on_an_agent_reach_the_hub()
    {
        await PairAndSyncAsync();
        _hub.WithHub((hub, now) => { hub.SetMember(hub.Config.Member("yoav") with { LogSearches = true, JumpGuard = true, BlockAds = true }, now); return 0; }, "parent");
        await Eventually(() => _agent.AgentSnapshotVersion == HubVersion, "settings with logSearches on the agent");
        Assert.True(_agent.WithAgent((agent, now) => ExtensionPolicy.For(agent.View(), agent.AccountOf("yoav"), now).Features.BlockAds)); // §12 travels with the settings

        Assert.True(_agent.WithAgent((agent, now) => agent.ReportSearch(agent.AccountOf("yoav"), "bing", "minecraft seeds", "https://www.bing.com/search?q=minecraft+seeds", now)));
        _agent.WithAgent((agent, now) => { agent.ReportOverride(agent.AccountOf("yoav"), "https://other.example/", Core.Decisions.ReasonCode.BlockedJump, now); return 0; });
        _agent.ReportTamper("S-1-5-21-3000-1001", TamperKinds.NetworkRepaired); // yoav's account on laptop-yoav
        _sync.Wake();
        await Eventually(() => _hub.WithHub((hub, _) => hub.Events.Count(e => e.Type is EventType.Search or EventType.Override or EventType.Tamper)) == 3, "events uploaded");

        var hubAdmin = new Pipes.AdminPipeServer(_hub, Options.Create(_hub.Options), _clock, NullLogger<Pipes.AdminPipeServer>.Instance);
        var search = Assert.Single(Assert.IsType<Contracts.Admin.EventsResult>(hubAdmin.Handle(new Contracts.Admin.GetEvents(EventType: "search"), "parent")).Events);
        Assert.Equal(("yoav", "laptop-yoav", "bing:minecraft seeds", "https://www.bing.com/search?q=minecraft+seeds"), (search.MemberId, search.DeviceId, search.Detail, search.Url));
        var overridden = Assert.Single(Assert.IsType<Contracts.Admin.EventsResult>(hubAdmin.Handle(new Contracts.Admin.GetEvents(EventType: "override"), "parent")).Events);
        Assert.Equal(("yoav", "blocked_jump", "https://other.example/"), (overridden.MemberId, overridden.Reason, overridden.Url));

        var tamper = Assert.Single(_hub.NotificationsSince(null), n => n.Kind == "tamper");
        Assert.Equal(("network_repaired", "yoav", "laptop-yoav"), (tamper.Detail, tamper.MemberId, tamper.DeviceId));

        // A retried upload (same event ids) records nothing twice.
        _sync.Wake();
        await Task.Delay(300);
        Assert.Equal(3, _hub.WithHub((hub, _) => hub.Events.Count(e => e.Type is EventType.Search or EventType.Override or EventType.Tamper)));
        Assert.Single(_hub.NotificationsSince(null), n => n.Kind == "tamper");
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

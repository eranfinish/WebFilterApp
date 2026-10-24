using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Agent;
using WebFilter.Contracts.Notify;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Service.Accounts;
using WebFilter.Service.Guard;
using WebFilter.Service.Pipes;

namespace WebFilter.Service.Tests;

/// <summary>
/// Level-2 checks with real named pipes, real SQLite and OS-reported client identity. The test process plays the
/// native host (it is configured as the trusted client) and the admin app (it is the service account).
/// </summary>
public sealed class PipeIntegrationTests : IAsyncLifetime
{
    private static readonly string UserSid = WindowsIdentity.GetCurrent().User!.Value;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-pipes-" + Guid.NewGuid().ToString("N"));
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8];
    private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-11-01T16:00:00+02:00"));
    private ServiceOptions _options = null!;
    private FamilyService _family = null!;
    private AgentPipeServer _agentServer = null!;
    private AdminPipeServer _adminServer = null!;
    private NotifyPipeServer _notifyServer = null!;
    private Func<ClientIdentity, bool>? _notifyPolicy;
    private readonly FakeLocalAccounts _accounts = new();
    private readonly ExtensionConnections _connections = new();

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        // The family fixture, with noa's account on pc-kids belonging to whoever runs the tests.
        var bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "tests", "simulation", "fixtures", "family-base.json")))!;
        foreach (var account in bundle["devices"]!.AsArray().First(d => (string)d!["id"]! == "pc-kids")!["accounts"]!.AsArray())
            if ((string?)account!["member"] == "noa") account["accountId"] = UserSid;
        var bundlePath = Path.Combine(_dir, "bootstrap.json");
        File.WriteAllText(bundlePath, bundle.ToJsonString());

        _options = new ServiceOptions
        {
            Role = "hub",
            DeviceId = "pc-kids",
            DataDirectory = Path.Combine(_dir, "data"),
            ProfileDirectory = Path.Combine(_dir, "profiles"),
            AgentPipeName = $"WebFilter.Test.Agent.{_suffix}",
            AdminPipeName = $"WebFilter.Test.Admin.{_suffix}",
            NotifyPipeName = $"WebFilter.Test.Notify.{_suffix}",
            TrustedClientPaths = [Environment.ProcessPath!],
            BootstrapBundlePath = bundlePath,
        };
        await StartAsync();
    }

    private async Task StartAsync()
    {
        _family = new FamilyService(Options.Create(_options), _clock, NullLogger<FamilyService>.Instance);
        _family.Start();
        _agentServer = new AgentPipeServer(_family, Options.Create(_options), NullLogger<AgentPipeServer>.Instance, _connections);
        _adminServer = new AdminPipeServer(_family, Options.Create(_options), _clock, NullLogger<AdminPipeServer>.Instance, _accounts);
        _notifyServer = _notifyPolicy is null
            ? new NotifyPipeServer(_family, Options.Create(_options), NullLogger<NotifyPipeServer>.Instance)
            : new NotifyPipeServer(_family, Options.Create(_options), NullLogger<NotifyPipeServer>.Instance) { IsAllowed = _notifyPolicy };
        await _agentServer.StartAsync(CancellationToken.None);
        await _adminServer.StartAsync(CancellationToken.None);
        await _notifyServer.StartAsync(CancellationToken.None);
    }

    private async Task StopAsync()
    {
        await _agentServer.StopAsync(CancellationToken.None);
        await _adminServer.StopAsync(CancellationToken.None);
        await _notifyServer.StopAsync(CancellationToken.None);
        _family.Dispose();
    }

    public async Task DisposeAsync()
    {
        await StopAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task Hello_identifies_the_os_user_and_maps_it_to_the_family_member()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        var hello = await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge", "abcdefghijklmnopabcdefghijklmnop"));
        Assert.Equal(UserSid, hello.User.AccountId);
        Assert.Equal("child", hello.User.Role);
        Assert.Equal("allowlist", hello.Mode);
        Assert.Equal("service", hello.Source);
    }

    /// <summary>
    /// PROTECTION-SPEC §14: a hello tells the extension guard that this browser's extension is connected for the OS's user
    /// (not one the message names), every message after it that it is alive, and a closed session ages out.
    /// </summary>
    [Fact]
    public async Task A_hello_registers_the_extension_session_for_the_guard_and_its_messages_keep_it_alive()
    {
        const string ChromeId = "cccccccccccccccccccccccccccccccc";
        var hellos = 0;
        _connections.Connected += () => Interlocked.Increment(ref hellos);
        var start = _clock.Now;
        await using (var agent = await AgentClient.ConnectAsync(_options.AgentPipeName))
        {
            await agent.RequestAsync<TimeStateMessage>(new HeartbeatRequest(true, "active", true));
            Assert.Empty(_connections.Snapshot(start)); // no hello yet: no browser to count it for

            await agent.RequestAsync<HelloResponse>(new HelloRequest("0.3.0", "chrome", ChromeId));
            _clock.Now = start.AddSeconds(30);
            await agent.RequestAsync<TimeStateMessage>(new HeartbeatRequest(true, "active", true));

            var session = Assert.Single(_connections.Snapshot(_clock.Now));
            Assert.Equal((UserSid, BrowserKind.Chrome, ChromeId, start.AddSeconds(30)), (session.AccountId, session.Browser, session.ExtensionId, session.LastHeard));
            Assert.Equal(1, Volatile.Read(ref hellos));

            // A hello for a browser the guard does not know is answered, and counts for nothing.
            await using var other = await AgentClient.ConnectAsync(_options.AgentPipeName);
            await other.RequestAsync<HelloResponse>(new HelloRequest("0.3.0", "firefox", ChromeId));
            Assert.Single(_connections.Snapshot(_clock.Now));
        }

        // Closed: the last message counts for a while (an extension reloading reconnects within it), then it is forgotten.
        var gone = start.AddSeconds(30) + ExtensionConnections.ClosedKept + TimeSpan.FromSeconds(1);
        Assert.NotNull(await Poll(() => _connections.Snapshot(gone).Count == 0 ? "closed" : null));
        Assert.Equal(1, Volatile.Read(ref hellos));
    }

    /// <summary>PROTECTION-SPEC §14 and §10: a browser blocked for lack of the extension reaches the parents as a tamper notification naming the browser.</summary>
    [Fact]
    public async Task A_missing_extension_becomes_a_tamper_notification_naming_the_browser()
    {
        // Whoever runs the tests plays the parent (the identity check itself is tested above).
        await StopAsync();
        _notifyPolicy = _ => true;
        await StartAsync();
        await using var notify = await NotifyClient.ConnectAsync(_options.NotifyPipeName);

        _family.ReportTamper(UserSid, TamperKinds.ExtensionMissingIn(BrowserKind.Chrome));
        _family.ReportTamper(UserSid, TamperKinds.ExtensionMissingIn(BrowserKind.Chrome)); // within the hour: not again
        _family.ReportTamper(UserSid, TamperKinds.ExtensionMissingIn(BrowserKind.Edge));
        _family.Tick();

        var items = Assert.IsType<NotificationsResult>(await notify.RequestAsync(new GetNotifications())).Items;
        Assert.Equal([("tamper", "extension_missing:chrome"), ("tamper", "extension_missing:edge")], items.Select(i => (i.Kind, i.Detail)));
        Assert.All(items, i => Assert.Equal(("noa", "pc-kids"), (i.MemberId, i.DeviceId)));
    }

    [Fact]
    public async Task Heartbeats_report_the_member_time_state()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        var state = await agent.RequestAsync<TimeStateMessage>(new HeartbeatRequest(true, "active", true));
        Assert.Equal("ok", state.State);
        Assert.Equal(120, state.RemainingMinutes);
        Assert.Equal("19:00", state.WindowEndsAt);
    }

    [Fact]
    public async Task Rules_carry_compiled_dnr_rules_and_youtube_policy()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge", "abcdefghijklmnopabcdefghijklmnop"));
        var rules = await agent.RequestAsync<RulesResponse>(new GetRulesRequest());
        Assert.NotEmpty(rules.DynamicRules);
        Assert.Contains(rules.DynamicRules, r => r.ToString().Contains("abcdefghijklmnopabcdefghijklmnop", StringComparison.Ordinal));
        Assert.Equal("allowlist", rules.YouTube.Mode);
        Assert.Contains(rules.YouTube.Allowed, c => c.ChannelId == "UCkhanKids00000000000001");
    }

    [Fact]
    public async Task The_block_page_learns_why_without_recording_another_block()
    {
        _family.WithHub((hub, now) =>
        {
            var config = hub.Config.Clone();
            config.MemberCategories.Add(new Core.Model.MemberCategory("noa", "social", Core.Model.RuleAction.Block));
            hub.ReplaceSettings(config, now);
            return 0;
        });
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge"));
        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);
        var before = Assert.IsType<EventsResult>(await admin.RequestAsync(new GetEvents())).Events.Count;

        var social = await agent.RequestAsync<DecideUrlResponse>(new DecideUrlRequest("https://www.instagram.com/explore"));
        Assert.Equal(("block", "blocked_category", "social", true, "url"), (social.Decision, social.Reason, social.Category, social.CanRequest, social.RequestKind));
        var other = await agent.RequestAsync<DecideUrlResponse>(new DecideUrlRequest("https://unknown.example/"));
        Assert.Equal(("block", "blocked_not_in_allowlist"), (other.Decision, other.Reason));
        var school = await agent.RequestAsync<DecideUrlResponse>(new DecideUrlRequest("https://mashov.info/"));
        Assert.Equal("allow", school.Decision);

        Assert.Equal(before, Assert.IsType<EventsResult>(await admin.RequestAsync(new GetEvents())).Events.Count);
    }

    [Fact]
    public async Task Report_block_records_with_the_service_reason_and_unknown_messages_keep_the_session()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge"));
        await agent.SendRawAsync("""{"type":"somethingNewer","v":2,"id":"x1"}""");
        Assert.Equal("unsupported", (await agent.WaitForAsync<ErrorMessage>(m => m.Id == "x1"))!.Code);

        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);
        async Task<int> Blocks() => Assert.IsType<EventsResult>(await admin.RequestAsync(new GetEvents(EventType: "blocked"))).Events.Count;
        var before = await Blocks();

        var result = await agent.RequestAsync<ReportBlockResult>(new ReportBlockRequest("https://unknown.example/", "blocked_rule", null));
        Assert.Equal(("block", "blocked_not_in_allowlist", true), (result.Decision, result.Reason, result.CanRequest));
        Assert.Equal(before + 1, await Blocks());

        var stale = await agent.RequestAsync<ReportBlockResult>(new ReportBlockRequest("https://mashov.info/", "blocked_rule", null));
        Assert.Equal("allow", stale.Decision);
        Assert.NotNull(await agent.WaitForAsync<RulesChangedMessage>());
        Assert.Equal(before + 1, await Blocks());

        Assert.Equal("ok", (await agent.RequestAsync<ErrorMessage>(new ErrorMessage("dnr_rules_dropped", "rules 1001, 1002"))).Code);
        var problems = Assert.IsType<EventsResult>(await admin.RequestAsync(new GetEvents(EventType: "service"))).Events;
        Assert.Contains(problems, e => e.Detail == "dnr_rules_dropped: rules 1001, 1002");
    }

    [Fact]
    public async Task Admin_changes_record_who_made_them_and_survive_a_restart()
    {
        await using (var admin = await AdminClient.ConnectAsync(_options.AdminPipeName))
        {
            Assert.IsType<AdminOk>(await admin.RequestAsync(new Pause("noa", _clock.GetUtcNow().AddMinutes(30))));
            var rule = new Contracts.Hub.RuleDto("r-dup", "noa", "dup.example", "domain", "block", false, null);
            Assert.IsType<AdminOk>(await admin.RequestAsync(new AddRule(rule)));
            var duplicate = Assert.IsType<AdminError>(await admin.RequestAsync(new AddRule(rule with { Pattern = "other.example" })));
            Assert.Contains("already exists", duplicate.Message);
        }
        _family.Flush();
        await StopAsync();
        await StartAsync();

        await using var again = await AdminClient.ConnectAsync(_options.AdminPipeName);
        var changes = Assert.IsType<EventsResult>(await again.RequestAsync(new GetEvents(EventType: "admin_change"))).Events;
        var expected = WindowsIdentity.GetCurrent().Name;
        Assert.Contains(changes, e => e.Detail == "pause" && e.Admin == expected);
        Assert.Contains(changes, e => e.Detail == "add_rule" && e.Admin == expected);
    }

    [Fact]
    public async Task A_pause_lifts_the_browser_rules_until_it_ends()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge", "abcdefghijklmnopabcdefghijklmnop"));
        Assert.NotEmpty((await agent.RequestAsync<RulesResponse>(new GetRulesRequest())).DynamicRules);

        _family.WithHub((hub, now) => { hub.Pause("noa", now.AddMinutes(20), now); return 0; });
        Assert.NotNull(await agent.WaitForAsync<RulesChangedMessage>());
        var paused = await agent.RequestAsync<RulesResponse>(new GetRulesRequest());
        Assert.Empty(paused.DynamicRules);
        Assert.Equal(("unrestricted", "unlimited"), (paused.YouTube.Mode, paused.Time.State));

        _clock.Now = _clock.Now.AddMinutes(21);
        _family.Tick();
        Assert.NotNull(await Poll(() => agent.CountOf<RulesChangedMessage>() >= 2 ? "pushed" : null));
        var after = await agent.RequestAsync<RulesResponse>(new GetRulesRequest());
        Assert.NotEmpty(after.DynamicRules);
        Assert.Equal("allowlist", after.YouTube.Mode);
    }

    [Fact]
    public async Task An_expiring_rule_makes_the_agent_fetch_rules_again()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge", "abcdefghijklmnopabcdefghijklmnop"));
        _family.WithHub((hub, now) =>
        {
            hub.AddRule(new Core.Model.Rule("r-temp", "noa", "temp.example", Core.Model.PatternType.Domain, Core.Model.RuleAction.Allow, ExpiresAt: now.AddMinutes(10)), now);
            return 0;
        });
        Assert.NotNull(await agent.WaitForAsync<RulesChangedMessage>());
        var before = await agent.RequestAsync<RulesResponse>(new GetRulesRequest());
        Assert.Contains(before.DynamicRules, r => r.ToString().Contains("temp.example", StringComparison.Ordinal));

        _family.Tick();
        await Task.Delay(300);
        Assert.Equal(1, agent.CountOf<RulesChangedMessage>());

        _clock.Now = _clock.Now.AddMinutes(11);
        _family.Tick();
        Assert.NotNull(await Poll(() => agent.CountOf<RulesChangedMessage>() == 2 ? "pushed" : null));
        var after = await agent.RequestAsync<RulesResponse>(new GetRulesRequest());
        Assert.DoesNotContain(after.DynamicRules, r => r.ToString().Contains("temp.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Admin_changes_are_pushed_to_connected_agents()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge"));
        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);
        Assert.IsType<AdminOk>(await admin.RequestAsync(new Pause("noa", _clock.GetUtcNow().AddMinutes(30))));
        Assert.NotNull(await agent.WaitForAsync<RulesChangedMessage>());
        var status = Assert.IsType<StatusResult>(await admin.RequestAsync(new GetStatus()));
        Assert.NotNull(status.Members.Single(m => m.MemberId == "noa").PausedUntil);
    }

    [Fact]
    public async Task Access_request_flows_to_the_admin_and_the_decision_back_to_the_child()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge"));
        var created = await agent.RequestAsync<RequestAccessResponse>(new RequestAccessRequest("url", "https://pbskids.org/games", null, "homework"));
        Assert.Equal("created", created.Result);

        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);
        var pending = Assert.IsType<RequestsResult>(await admin.RequestAsync(new GetRequests("pending")));
        var request = Assert.Single(pending.Requests);
        Assert.Equal("pbskids.org", request.TargetKey);
        Assert.IsType<AdminOk>(await admin.RequestAsync(new DecideRequest(request.Id, "approve", Minutes: 15)));

        var decided = await agent.WaitForAsync<AccessDecidedMessage>();
        Assert.Equal(request.Id, decided!.RequestId);
        Assert.Equal("approved", decided.Status);
    }

    [Fact]
    public async Task Settings_survive_a_service_restart()
    {
        await using (var admin = await AdminClient.ConnectAsync(_options.AdminPipeName))
            Assert.IsType<AdminOk>(await admin.RequestAsync(new AddRule(new Contracts.Hub.RuleDto("r-test", "noa", "persist.example", "domain", "allow"))));
        await StopAsync();
        await StartAsync();
        await using var again = await AdminClient.ConnectAsync(_options.AdminPipeName);
        var settings = Assert.IsType<SettingsResult>(await again.RequestAsync(new GetSettings()));
        Assert.Contains(settings.Settings.Rules, r => r.Id == "r-test");
    }

    [Fact]
    public async Task Scanning_adds_this_computers_new_accounts_unlinked_and_keeps_the_links()
    {
        _accounts.Set(
            new DeviceAccount(UserSid, "noa", null, IsAdmin: false),
            new DeviceAccount("S-1-5-21-2000-1010", "kids-guest", null, IsAdmin: false),
            new DeviceAccount("S-1-5-21-2000-1011", "parent2", null, IsAdmin: true));
        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);

        Assert.Equal("2", Assert.IsType<AdminOk>(await admin.RequestAsync(new ScanAccounts())).Detail);

        var settings = Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings;
        var accounts = settings.Devices.Single(d => d.Id == "pc-kids").Accounts;
        Assert.Equal("noa", accounts.Single(a => a.AccountId == UserSid).Member);
        Assert.Equal(new Contracts.Hub.AccountDto("S-1-5-21-2000-1010", "kids-guest", null, false), accounts.Single(a => a.Name == "kids-guest"));
        Assert.True(accounts.Single(a => a.Name == "parent2").IsAdmin);
        Assert.Equal(7, accounts.Count);

        // Nothing new: nothing changes, not even the version.
        Assert.Equal("0", Assert.IsType<AdminOk>(await admin.RequestAsync(new ScanAccounts())).Detail);
        Assert.Equal(settings.Version, Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings.Version);
    }

    [Fact]
    public async Task Replacing_settings_read_before_a_change_is_refused_and_changes_nothing()
    {
        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);
        var read = Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings;
        // Meanwhile (another window, a request approval): a new rule.
        Assert.IsType<AdminOk>(await admin.RequestAsync(new AddRule(new Contracts.Hub.RuleDto("r-new", "noa", "new.example", "domain", "allow"))));

        var stale = read with { Rules = [] };
        var conflict = Assert.IsType<AdminError>(await admin.RequestAsync(new ReplaceSettings(stale, IfVersion: read.Version)));
        Assert.Equal(ReplaceSettings.ConflictCode, conflict.Code);

        var current = Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings;
        Assert.Equal(["r-school", "r-new"], current.Rules.Select(r => r.Id));

        // Read again, change, replace at the current version: accepted, and the rule added meanwhile is kept.
        var edited = current with { Members = [.. current.Members.Select(m => m.Id == "savta" ? m with { DisplayName = "סבתא רבה" } : m)] };
        Assert.IsType<AdminOk>(await admin.RequestAsync(new ReplaceSettings(edited, IfVersion: current.Version)));
        var after = Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings;
        Assert.Equal("סבתא רבה", after.Members.Single(m => m.Id == "savta").DisplayName);
        Assert.Contains(after.Rules, r => r.Id == "r-new");
        Assert.True(after.Version > current.Version);
    }

    [Fact]
    public async Task Replacing_settings_with_an_invalid_rule_is_refused_with_the_reason()
    {
        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);
        var read = Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings;
        var bad = read with { Rules = [.. read.Rules, new Contracts.Hub.RuleDto("r-bad", "noa", "(?<=a)b", "regex", "block")] };

        var error = Assert.IsType<AdminError>(await admin.RequestAsync(new ReplaceSettings(bad, IfVersion: read.Version)));

        Assert.Equal("bad_request", error.Code);
        Assert.StartsWith("Rule 'r-bad':", error.Message);
        Assert.Equal(read.Rules, Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings.Rules);
    }

    [Fact]
    public async Task Settings_that_refer_to_a_missing_member_or_hold_a_malformed_time_are_refused_and_change_nothing()
    {
        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);
        var read = Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings;

        // savta's Windows account on pc-kids still points at her.
        var dangling = Assert.IsType<AdminError>(await admin.RequestAsync(new ReplaceSettings(read with { Members = [.. read.Members.Where(m => m.Id != "savta")] })));
        Assert.Equal("bad_request", dangling.Code);
        Assert.StartsWith("Account 'savta' on 'pc-kids' refers to unknown member 'savta'.", dangling.Message);
        var malformed = read with { Schedules = [.. read.Schedules, new Contracts.Hub.ScheduleDto("noa", [1], "9:00", "12:00")] };
        Assert.Equal("bad_request", Assert.IsType<AdminError>(await admin.RequestAsync(new ReplaceSettings(malformed))).Code);

        // The same connection still answers, and the settings are as they were.
        var after = Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings;
        Assert.Equal(read.Version, after.Version);
        Assert.Equal(read.Members, after.Members);
        Assert.IsType<StatusResult>(await admin.RequestAsync(new GetStatus()));
    }

    [Fact]
    public async Task The_hub_answers_the_requirement_checks_from_this_computer()
    {
        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);

        var checks = Assert.IsType<DiagnosticsResult>(await admin.RequestAsync(new GetDiagnostics())).Checks;

        // A test run: not LocalSystem, no browser policies, no extension ids — and a network check, as on any hub.
        Assert.Equal(("warn", "warn", "warn"), (checks.Single(c => c.Id == "service").State, checks.Single(c => c.Id == "browser_policies").State,
            checks.Single(c => c.Id == "extension").State));
        Assert.Contains(checks, c => c.Id == "network");
    }

    [Fact]
    public void The_windows_account_reader_lists_enabled_local_accounts_by_sid()
    {
        var accounts = new WindowsLocalAccounts().List();

        Assert.All(accounts, a =>
        {
            Assert.StartsWith("S-1-5-21-", a.AccountId);
            Assert.False(string.IsNullOrWhiteSpace(a.Name));
            Assert.Null(a.MemberId);
        });
        Assert.Equal(accounts.Count, accounts.Select(a => a.AccountId).Distinct().Count());
        // Disabled by default on every Windows install.
        Assert.DoesNotContain(accounts, a => a.Name is "DefaultAccount" or "WDAGUtilityAccount");
    }

    [Fact]
    public async Task Untrusted_programs_are_refused()
    {
        await StopAsync();
        _options.TrustedClientPaths = [@"C:\Program Files\WebFilter\WebFilter.NativeHost.exe"];
        await StartAsync();
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        var error = await agent.WaitForAsync<ErrorMessage>();
        Assert.Equal("untrusted_client", error!.Code);
    }

    // ---- PROTECTION-SPEC §1, §4, §6 over the agent and admin pipes ----------------------------------------------------

    [Fact]
    public async Task Rules_carry_the_members_protection_features_and_an_admin_sets_them()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge", "abcdefghijklmnopabcdefghijklmnop"));
        var before = await agent.RequestAsync<RulesResponse>(new GetRulesRequest());
        Assert.Equal(new AgentFeatures(), before.Features);
        static int Builtins(RulesResponse rules) => rules.DynamicRules.Count(r => r.GetProperty("priority").GetInt32() == Core.Dnr.DnrPriorities.BuiltinBlock);
        Assert.Equal(1, Builtins(before)); // Google's AI Mode: noa is in allowlist mode and ai-chat is not allowed for her

        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);
        Assert.IsType<AdminOk>(await admin.RequestAsync(new SetMember("noa", BlockImageSearch: true, JumpGuard: true, BlockYouTubeShorts: true, LogSearches: true)));
        Assert.NotNull(await agent.WaitForAsync<RulesChangedMessage>());

        var after = await agent.RequestAsync<RulesResponse>(new GetRulesRequest());
        Assert.Equal(new AgentFeatures(JumpGuard: true, BlockYouTubeShorts: true, LogSearches: true, CanOverrideJump: false), after.Features); // a child
        Assert.True(Builtins(after) > Builtins(before));
        var noa = Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings.Members.Single(m => m.Id == "noa");
        Assert.Equal((true, true, true, true), (noa.BlockImageSearch, noa.JumpGuard, noa.BlockYouTubeShorts, noa.LogSearches));

        // A patch leaves the other flags alone.
        Assert.IsType<AdminOk>(await admin.RequestAsync(new SetMember("noa", JumpGuard: false)));
        noa = Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings.Members.Single(m => m.Id == "noa");
        Assert.Equal((true, false, true, true), (noa.BlockImageSearch, noa.JumpGuard, noa.BlockYouTubeShorts, noa.LogSearches));

        var image = await agent.RequestAsync<DecideUrlResponse>(new DecideUrlRequest("https://www.google.com/search?q=cats&udm=2"));
        Assert.Equal(("block", "blocked_image_search", "builtin-image-search-google", false), (image.Decision, image.Reason, image.RuleId, image.CanRequest));
        var aiMode = await agent.RequestAsync<DecideUrlResponse>(new DecideUrlRequest("https://www.google.com/search?q=cats&udm=50"));
        Assert.Equal(("block", "blocked_rule", "builtin-ai-mode-google", false), (aiMode.Decision, aiMode.Reason, aiMode.RuleId, aiMode.CanRequest));
    }

    /// <summary>PROTECTION-SPEC §12: the ad blocking switch reaches the rules reply on its own, and a pause turns it off with the rest of the filtering.</summary>
    [Fact]
    public async Task Ad_blocking_follows_its_switch_and_stops_while_paused()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge", "abcdefghijklmnopabcdefghijklmnop"));
        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);
        async Task<AgentFeatures?> Features() => (await agent.RequestAsync<RulesResponse>(new GetRulesRequest())).Features;
        Assert.Equal(new AgentFeatures(), await Features());

        Assert.IsType<AdminOk>(await admin.RequestAsync(new SetMember("noa", BlockAds: true)));
        Assert.NotNull(await agent.WaitForAsync<RulesChangedMessage>());
        Assert.Equal(new AgentFeatures(BlockAds: true), await Features());
        var noa = Assert.IsType<SettingsResult>(await admin.RequestAsync(new GetSettings())).Settings.Members.Single(m => m.Id == "noa");
        Assert.Equal((true, false, false, false, false), (noa.BlockAds, noa.BlockImageSearch, noa.JumpGuard, noa.BlockYouTubeShorts, noa.LogSearches));

        Assert.IsType<AdminOk>(await admin.RequestAsync(new Pause("noa", _clock.GetUtcNow().AddMinutes(30))));
        Assert.Equal(new AgentFeatures(), await Features());
        Assert.IsType<AdminOk>(await admin.RequestAsync(new Resume("noa")));
        Assert.Equal(new AgentFeatures(BlockAds: true), await Features());

        Assert.IsType<AdminOk>(await admin.RequestAsync(new SetMember("noa", BlockAds: false)));
        Assert.Equal(new AgentFeatures(), await Features());
    }

    [Fact]
    public async Task Searches_are_recorded_only_while_the_members_searches_are_logged()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge"));
        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);
        async Task<IReadOnlyList<EventDto>> Searches() => Assert.IsType<EventsResult>(await admin.RequestAsync(new GetEvents(EventType: "search"))).Events;

        var off = await agent.RequestAsync<ErrorMessage>(new ReportSearchRequest("google", "cats", "https://www.google.com/search?q=cats"));
        Assert.Equal(("ok", "not recorded"), (off.Code, off.Message));
        Assert.Empty(await Searches());

        Assert.IsType<AdminOk>(await admin.RequestAsync(new SetMember("noa", LogSearches: true)));
        var on = await agent.RequestAsync<ErrorMessage>(new ReportSearchRequest("youtube", "lego: city", "https://www.youtube.com/results?search_query=lego"));
        Assert.Equal(("ok", "recorded"), (on.Code, on.Message));
        var search = Assert.Single(await Searches());
        Assert.Equal(("noa", "pc-kids", "youtube:lego: city", "https://www.youtube.com/results?search_query=lego"), (search.MemberId, search.DeviceId, search.Detail, search.Url));

        Assert.Equal("bad_request", (await agent.RequestAsync<ErrorMessage>(new ReportSearchRequest("Google Search", "x", "https://x/"))).Code);
        Assert.Single(await Searches());
    }

    [Fact]
    public async Task Overrides_are_recorded_and_blocks_only_the_extension_can_judge_keep_its_reason()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge"));
        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);

        Assert.Equal("ok", (await agent.RequestAsync<ErrorMessage>(new ReportOverrideRequest("https://mashov.info/grades", "blocked_jump"))).Code);
        var overridden = Assert.Single(Assert.IsType<EventsResult>(await admin.RequestAsync(new GetEvents(EventType: "override"))).Events);
        Assert.Equal(("noa", "blocked_jump", "https://mashov.info/grades"), (overridden.MemberId, overridden.Reason, overridden.Url));

        // mashov.info is always allowed, yet a jump to it was blocked: the extension's reason stands, and nothing is stale.
        var jump = await agent.RequestAsync<ReportBlockResult>(new ReportBlockRequest("https://mashov.info/", "blocked_jump", null));
        Assert.Equal(("block", "blocked_jump", false), (jump.Decision, jump.Reason, jump.CanRequest));
        var shorts = await agent.RequestAsync<ReportBlockResult>(new ReportBlockRequest("https://www.youtube.com/shorts/abc", "blocked_youtube_shorts", null));
        Assert.Equal(("block", "blocked_youtube_shorts", false, "youtube"), (shorts.Decision, shorts.Reason, shorts.CanRequest, shorts.RequestKind));
        var blocks = Assert.IsType<EventsResult>(await admin.RequestAsync(new GetEvents(EventType: "blocked"))).Events;
        Assert.Contains(blocks, e => e.Reason == "blocked_jump" && e.Url == "https://mashov.info/");
        Assert.Contains(blocks, e => e.Reason == "blocked_youtube_shorts");
        Assert.Equal(0, agent.CountOf<RulesChangedMessage>());
    }

    // ---- PROTECTION-SPEC §13: the parents' baseline protection on the wire --------------------------------------------

    /// <summary>The test's own account becomes dana's (an unrestricted adult), with the adult list in the family's categories.</summary>
    private void LinkTheUserToAnAdult(Core.Model.BaselineProtection? baseline = null) => _family.WithHub((hub, now) =>
    {
        var config = hub.Config.Clone();
        var kids = config.Devices.FindIndex(d => d.Id == "pc-kids");
        config.Devices[kids] = config.Devices[kids] with
        {
            Accounts = [.. config.Devices[kids].Accounts.Select(a => a.AccountId == UserSid ? a with { MemberId = "dana" } : a)],
        };
        config.Categories["adult"] = ["adult-site.example", "nsfw.example"];
        hub.ReplaceSettings(baseline is null ? config : PolicyTests.WithBaseline(config, baseline), now);
        return 0;
    });

    [Fact]
    public async Task A_parent_gets_mode_baseline_with_the_adult_rules_and_the_switches_as_features()
    {
        LinkTheUserToAnAdult();
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        var hello = await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge", "abcdefghijklmnopabcdefghijklmnop"));
        Assert.Equal(("adult", "baseline"), (hello.User.Role, hello.Mode));

        var rules = await agent.RequestAsync<RulesResponse>(new GetRulesRequest());
        Assert.Equal("baseline", rules.Mode);
        Assert.Equal(new AgentFeatures(JumpGuard: true, BlockYouTubeShorts: false, LogSearches: false, CanOverrideJump: true, BlockAds: true), rules.Features);
        Assert.Equal(("unrestricted", "unlimited"), (rules.YouTube.Mode, rules.Time.State));
        Assert.Empty(rules.TimeUpSessionRules);
        var block = Assert.Single(rules.DynamicRules, r => r.GetProperty("action").GetProperty("type").GetString() == "redirect");
        Assert.Equal(["adult-site.example", "nsfw.example"], block.GetProperty("condition").GetProperty("requestDomains").EnumerateArray().Select(d => d.GetString()));
        Assert.Contains("chrome-extension://abcdefghijklmnopabcdefghijklmnop/blocked.html", block.ToString(), StringComparison.Ordinal);
        // No default block, IP-literal gate or YouTube rule: only the list and the parents' allow rules.
        Assert.DoesNotContain(rules.DynamicRules, r => r.GetProperty("id").GetInt32() < 1000);

        var adult = await agent.RequestAsync<DecideUrlResponse>(new DecideUrlRequest("https://www.adult-site.example/x"));
        Assert.Equal(("block", "blocked_baseline_adult", false), (adult.Decision, adult.Reason, adult.CanRequest));
        var router = await agent.RequestAsync<DecideUrlResponse>(new DecideUrlRequest("http://192.168.1.1/"));
        Assert.Equal(("allow", "allowed_unrestricted"), (router.Decision, router.Reason));
        Assert.Equal("unlimited", (await agent.RequestAsync<TimeStateMessage>(new HeartbeatRequest(true, "active", true))).State);
    }

    [Fact]
    public async Task Nothing_a_parent_does_is_recorded_or_requested()
    {
        LinkTheUserToAnAdult();
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge"));
        await using var admin = await AdminClient.ConnectAsync(_options.AdminPipeName);
        async Task<int> Events(string type) => Assert.IsType<EventsResult>(await admin.RequestAsync(new GetEvents(EventType: type))).Events.Count;
        var blocksBefore = await Events("blocked");

        var reported = await agent.RequestAsync<ReportBlockResult>(new ReportBlockRequest("https://nsfw.example/", "blocked_rule", null));
        Assert.Equal(("block", "blocked_baseline_adult", false), (reported.Decision, reported.Reason, reported.CanRequest));
        var jump = await agent.RequestAsync<ReportBlockResult>(new ReportBlockRequest("https://other.example/", "blocked_jump", null));
        Assert.Equal(("block", "blocked_jump", false), (jump.Decision, jump.Reason, jump.CanRequest));
        var overridden = await agent.RequestAsync<ErrorMessage>(new ReportOverrideRequest("https://other.example/", "blocked_jump"));
        Assert.Equal(("ok", "not recorded"), (overridden.Code, overridden.Message));
        var search = await agent.RequestAsync<ErrorMessage>(new ReportSearchRequest("google", "anything", "https://www.google.com/search?q=anything"));
        Assert.Equal(("ok", "not recorded"), (search.Code, search.Message));
        Assert.Equal("unavailable", (await agent.RequestAsync<RequestAccessResponse>(new RequestAccessRequest("url", "https://nsfw.example/", null, null))).Result);
        await agent.RequestAsync<TimeStateMessage>(new HeartbeatRequest(true, "active", true));

        Assert.Equal(blocksBefore, await Events("blocked"));
        Assert.Equal(0, await Events("override"));
        Assert.Equal(0, await Events("search"));
        Assert.Empty(Assert.IsType<RequestsResult>(await admin.RequestAsync(new GetRequests("pending"))).Requests);
    }

    [Fact]
    public async Task With_every_switch_off_a_parent_is_unrestricted_again_and_a_change_reaches_the_extension_with_the_rules()
    {
        LinkTheUserToAnAdult();
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        Assert.Equal("baseline", (await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge"))).Mode);

        LinkTheUserToAnAdult(new Core.Model.BaselineProtection(BlockAdult: false, JumpGuard: false, BlockAds: false, SafeSearch: false));
        Assert.NotNull(await agent.WaitForAsync<RulesChangedMessage>());
        var off = await agent.RequestAsync<RulesResponse>(new GetRulesRequest());
        Assert.Equal("unrestricted", off.Mode);
        Assert.Empty(off.DynamicRules);
        Assert.Equal(new AgentFeatures(), off.Features);
        var adult = await agent.RequestAsync<DecideUrlResponse>(new DecideUrlRequest("https://adult-site.example/"));
        Assert.Equal(("allow", "allowed_unrestricted"), (adult.Decision, adult.Reason));

        // Only blockAdult off: still the baseline mode (the other switches), no rules.
        LinkTheUserToAnAdult(Core.Model.BaselineProtection.Default with { BlockAdult = false });
        Assert.NotNull(await Poll(() => agent.CountOf<RulesChangedMessage>() >= 2 ? "pushed" : null));
        var noAdult = await agent.RequestAsync<RulesResponse>(new GetRulesRequest());
        Assert.Equal(("baseline", 0), (noAdult.Mode, noAdult.DynamicRules.Count));
        Assert.True(noAdult.Features!.JumpGuard && noAdult.Features.BlockAds);
    }

    [Fact]
    public async Task A_childs_rules_say_their_mode_too()
    {
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge"));
        Assert.Equal("allowlist", (await agent.RequestAsync<RulesResponse>(new GetRulesRequest())).Mode);
    }

    // ---- PROTECTION-SPEC §10: the notify pipe, with real pipes and the OS-reported identity --------------------------

    /// <summary>
    /// Whether this test run's own account is an administrator's, worked out from its groups rather than the elevation type
    /// ClientIdentity reads: unelevated, an administrator's token holds Administrators for deny only; elevated, enabled.
    /// </summary>
    private static bool RunsAsAdministratorAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Claims.Any(c => c.Value == "S-1-5-32-544" &&
            c.Type is System.Security.Claims.ClaimTypes.GroupSid or System.Security.Claims.ClaimTypes.DenyOnlySid);
    }

    [Fact]
    public async Task The_notify_pipe_answers_an_administrator_account_with_requests_and_tamper_and_a_cursor()
    {
        await using var notify = await NotifyClient.ConnectAsync(_options.NotifyPipeName);
        if (!RunsAsAdministratorAccount())
        {
            // A standard account: the OS identity check refuses it, as it would a child's notifier.
            Assert.Equal(NotifyError.NotAdmin, Assert.IsType<NotifyError>(await notify.RequestAsync(new GetNotifications())).Code);
            return;
        }
        Assert.Empty(Assert.IsType<NotificationsResult>(await notify.RequestAsync(new GetNotifications())).Items);

        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        await agent.RequestAsync<HelloResponse>(new HelloRequest("0.0.1", "edge"));
        Assert.Equal("created", (await agent.RequestAsync<RequestAccessResponse>(new RequestAccessRequest("url", "https://www.pbskids.org/games", null, null))).Result);
        Assert.Equal("merged", (await agent.RequestAsync<RequestAccessResponse>(new RequestAccessRequest("url", "https://pbskids.org/other", null, null))).Result);
        Assert.Equal("created", (await agent.RequestAsync<RequestAccessResponse>(new RequestAccessRequest("time", null, 30, null))).Result);

        var items = Assert.IsType<NotificationsResult>(await notify.RequestAsync(new GetNotifications())).Items;
        Assert.Equal([("request", "url:pbskids.org"), ("request", "time:30")], items.Select(i => (i.Kind, i.Detail)));
        Assert.All(items, i => Assert.Equal(("noa", "pc-kids"), (i.MemberId, i.DeviceId)));
        Assert.Empty(Assert.IsType<NotificationsResult>(await notify.RequestAsync(new GetNotifications(items[^1].At))).Items);

        // A tamper repair on this computer: a notification, once per hour for the same account.
        _family.ReportTamper(UserSid, Core.Engine.TamperKinds.PolicyRepaired);
        _family.ReportTamper(UserSid, Core.Engine.TamperKinds.PolicyRepaired);
        _family.Tick();
        var tamper = Assert.Single(Assert.IsType<NotificationsResult>(await notify.RequestAsync(new GetNotifications(items[^1].At))).Items);
        Assert.Equal(("tamper", "policy_repaired", "noa", "pc-kids"), (tamper.Kind, tamper.Detail, tamper.MemberId, tamper.DeviceId));
        Assert.True(tamper.At > items[^1].At);

        // Unknown messages are answered, and the session goes on; the pipe is read only.
        Assert.Equal("unsupported", Assert.IsType<NotifyError>(await notify.RequestRawAsync("""{"type":"deleteNotifications","v":1,"id":"x9"}""", "x9")).Code);
        Assert.IsType<NotificationsResult>(await notify.RequestAsync(new GetNotifications()));
    }

    [Fact]
    public async Task The_notify_pipe_refuses_a_client_the_identity_check_rejects_and_hangs_up()
    {
        await StopAsync();
        _notifyPolicy = _ => false;
        await StartAsync();

        await using var notify = await NotifyClient.ConnectAsync(_options.NotifyPipeName);
        var refused = Assert.IsType<NotifyError>(await notify.RequestAsync(new GetNotifications()));
        Assert.Equal(NotifyError.NotAdmin, refused.Code);
        Assert.Null(await notify.ReadAsync());
    }

    [Fact]
    public async Task A_service_that_did_not_stop_cleanly_tells_the_parents_when_it_starts_again()
    {
        await StopAsync();
        File.WriteAllText(Path.Combine(_options.DataDirectory, FamilyService.RunningMarkerFileName), "crashed");
        await StartAsync();

        await using var notify = await NotifyClient.ConnectAsync(_options.NotifyPipeName);
        if (!RunsAsAdministratorAccount()) return;
        var tamper = Assert.Single(Assert.IsType<NotificationsResult>(await notify.RequestAsync(new GetNotifications())).Items);
        Assert.Equal(("tamper", "unclean_stop", "pc-kids", null), (tamper.Kind, tamper.Detail, tamper.DeviceId, tamper.MemberId));

        // A clean stop and start says nothing.
        await StopAsync();
        Assert.False(File.Exists(Path.Combine(_options.DataDirectory, FamilyService.RunningMarkerFileName)));
        await StartAsync();
        await using var again = await NotifyClient.ConnectAsync(_options.NotifyPipeName);
        Assert.Single(Assert.IsType<NotificationsResult>(await again.RequestAsync(new GetNotifications())).Items);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx"))) return dir.FullName;
        throw new InvalidOperationException("WebFilter.slnx not found");
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Plays the native host: sends requests and collects replies and pushes.</summary>
    private sealed class AgentClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly ConcurrentQueue<AgentMessage> _received = new();
        private readonly Task _reader;
        private int _next;

        private AgentClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = Task.Run(async () =>
            {
                try { while (await AgentProtocol.ReadAsync(_pipe) is { } m) _received.Enqueue(m); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException) { }
            });
        }

        public static async Task<AgentClient> ConnectAsync(string name)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            return new AgentClient(pipe);
        }

        public async Task<T> RequestAsync<T>(AgentMessage message) where T : AgentMessage
        {
            var id = $"t{Interlocked.Increment(ref _next)}";
            await AgentProtocol.WriteAsync(_pipe, message with { Id = id });
            var reply = await WaitForAsync<AgentMessage>(m => m.Id == id);
            return reply as T ?? throw new Xunit.Sdk.XunitException($"Expected {typeof(T).Name}, got {reply?.GetType().Name}: {(reply as ErrorMessage)?.Message}");
        }

        public int CountOf<T>() where T : AgentMessage => _received.OfType<T>().Count();

        public Task SendRawAsync(string json) => Contracts.Framing.MessageFraming.WriteAsync(_pipe, System.Text.Encoding.UTF8.GetBytes(json));

        public Task<T?> WaitForAsync<T>(Func<T, bool>? match = null) where T : AgentMessage => Poll(() =>
            _received.OfType<T>().FirstOrDefault(m => match?.Invoke(m) ?? true));

        public async ValueTask DisposeAsync()
        {
            await _pipe.DisposeAsync();
            await _reader;
        }
    }

    private sealed class AdminClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private int _next;

        private AdminClient(NamedPipeClientStream pipe) => _pipe = pipe;

        public static async Task<AdminClient> ConnectAsync(string name)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            return new AdminClient(pipe);
        }

        public async Task<AdminMessage> RequestAsync(AdminMessage message)
        {
            var id = $"a{Interlocked.Increment(ref _next)}";
            await AdminProtocol.WriteAsync(_pipe, message with { Id = id });
            var reply = await AdminProtocol.ReadAsync(_pipe) ?? throw new EndOfStreamException();
            Assert.Equal(id, reply.Id);
            return reply;
        }

        public ValueTask DisposeAsync() => _pipe.DisposeAsync();
    }

    /// <summary>Plays the notifier app.</summary>
    private sealed class NotifyClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private int _next;

        private NotifyClient(NamedPipeClientStream pipe) => _pipe = pipe;

        public static async Task<NotifyClient> ConnectAsync(string name)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(5000);
            return new NotifyClient(pipe);
        }

        public async Task<NotifyMessage> RequestAsync(NotifyMessage message)
        {
            var id = $"n{Interlocked.Increment(ref _next)}";
            await NotifyProtocol.WriteAsync(_pipe, message with { Id = id });
            var reply = await NotifyProtocol.ReadAsync(_pipe) ?? throw new EndOfStreamException();
            Assert.Equal(id, reply.Id);
            return reply;
        }

        public async Task<NotifyMessage> RequestRawAsync(string json, string id)
        {
            await Contracts.Framing.MessageFraming.WriteAsync(_pipe, System.Text.Encoding.UTF8.GetBytes(json));
            var reply = await NotifyProtocol.ReadAsync(_pipe) ?? throw new EndOfStreamException();
            Assert.Equal(id, reply.Id);
            return reply;
        }

        /// <summary>The next message, or null once the server has hung up.</summary>
        public async Task<NotifyMessage?> ReadAsync()
        {
            try { return await NotifyProtocol.ReadAsync(_pipe).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (IOException) { return null; }
        }

        public ValueTask DisposeAsync() => _pipe.DisposeAsync();
    }

    private static async Task<T?> Poll<T>(Func<T?> probe, int timeoutMs = 5000) where T : class
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } value) return value;
            await Task.Delay(20);
        }
        return null;
    }
}

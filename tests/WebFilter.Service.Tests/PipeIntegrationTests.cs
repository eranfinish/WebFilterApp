using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Agent;
using WebFilter.Core.Engine;
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
            TrustedClientPaths = [Environment.ProcessPath!],
            BootstrapBundlePath = bundlePath,
        };
        await StartAsync();
    }

    private async Task StartAsync()
    {
        _family = new FamilyService(Options.Create(_options), _clock, NullLogger<FamilyService>.Instance);
        _family.Start();
        _agentServer = new AgentPipeServer(_family, Options.Create(_options), NullLogger<AgentPipeServer>.Instance);
        _adminServer = new AdminPipeServer(_family, Options.Create(_options), _clock, NullLogger<AdminPipeServer>.Instance);
        await _agentServer.StartAsync(CancellationToken.None);
        await _adminServer.StartAsync(CancellationToken.None);
    }

    private async Task StopAsync()
    {
        await _agentServer.StopAsync(CancellationToken.None);
        await _adminServer.StopAsync(CancellationToken.None);
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
    public async Task Untrusted_programs_are_refused()
    {
        await StopAsync();
        _options.TrustedClientPaths = [@"C:\Program Files\WebFilter\WebFilter.NativeHost.exe"];
        await StartAsync();
        await using var agent = await AgentClient.ConnectAsync(_options.AgentPipeName);
        var error = await agent.WaitForAsync<ErrorMessage>();
        Assert.Equal("untrusted_client", error!.Code);
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

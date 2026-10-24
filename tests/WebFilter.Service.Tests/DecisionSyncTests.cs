using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Service.Mapping;

namespace WebFilter.Service.Tests;

/// <summary>
/// MVP-PLAN §8.1 between a hub and an agent service, with the snapshot passed as JSON instead of over HTTPS: the snapshot
/// carries the hub's recent decisions, the agent raises <see cref="FamilyService.RequestDecided"/> once for each decision
/// of a member with an account on it — also across a restart —, and the hub raises it only as the parent decides.
/// </summary>
public sealed class DecisionSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-decisions-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _clock = new(DateTimeOffset.Parse("2026-11-02T15:00:00+02:00"));
    private readonly FamilyService _hub;
    private readonly List<FamilyService> _agents = [];

    public DecisionSyncTests()
    {
        Directory.CreateDirectory(_dir);
        _hub = new FamilyService(Options.Create(new ServiceOptions
        {
            Role = "hub", DeviceId = "pc-parents", DataDirectory = Path.Combine(_dir, "hub"), ProfileDirectory = Path.Combine(_dir, "hub-profiles"),
            BootstrapBundlePath = Path.Combine(RepoRoot(), "tests", "simulation", "fixtures", "family-base.json"),
        }), _clock, NullLogger<FamilyService>.Instance);
        _hub.Start();
    }

    public void Dispose()
    {
        foreach (var agent in _agents) agent.Dispose();
        _hub.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void The_snapshot_carries_the_decisions_of_the_last_48_hours()
    {
        var id = Ask("laptop-yoav", "yoav", "https://www.ynet.co.il/news");
        _hub.Decide(id, approve: true, ApproveScope.Host, ApprovalDuration.For(30), null, "parent");

        var decision = Assert.Single(Fetch().Decisions!);

        Assert.Equal(new DecisionDto(id, "yoav", "laptop-yoav", "url", "ynet.co.il", "approved", _clock.Now, _clock.Now.AddMinutes(30)), decision);
        _clock.Now = _clock.Now.AddHours(49);
        Assert.Empty(Fetch().Decisions!);
    }

    [Fact]
    public void The_hub_announces_a_decision_once_as_the_parent_makes_it()
    {
        var announced = new List<RequestDecision>();
        _hub.RequestDecided += announced.Add;
        var id = Ask("pc-kids", "noa", "https://pbskids.org/games");

        _hub.Decide(id, approve: false, ApproveScope.Host, ApprovalDuration.Always, null, "parent");
        Fetch();
        _hub.Tick();
        _clock.Now = _clock.Now.AddMinutes(1);
        _hub.Tick();

        var decision = Assert.Single(announced);
        Assert.Equal((id, "noa", RequestStatus.Denied, (DateTimeOffset?)null), (decision.RequestId, decision.MemberId, decision.Status, decision.Until));
    }

    [Fact]
    public void An_agent_announces_each_decision_of_its_members_once_also_across_a_restart()
    {
        var (agent, announced) = StartAgent();
        agent.ApplyRemoteSnapshot(SyncMapper.FromDto(Fetch()));
        var yoavs = Ask("laptop-yoav", "yoav", "https://www.instagram.com/");
        var noas = Ask("pc-kids", "noa", "https://pbskids.org/games"); // noa has no account on laptop-yoav
        _hub.Decide(yoavs, approve: true, ApproveScope.Host, ApprovalDuration.Today, null, "parent");
        _hub.Decide(noas, approve: true, ApproveScope.Host, ApprovalDuration.Always, null, "parent");

        agent.ApplyRemoteSnapshot(SyncMapper.FromDto(Fetch()));

        var decision = Assert.Single(announced);
        Assert.Equal((yoavs, RequestStatus.Approved), (decision.RequestId, decision.Status));
        Assert.Equal(Core.Decisions.Decision.Allow, agent.WithAgent((a, now) => a.DecideUrl(a.AccountOf("yoav"), "https://www.instagram.com/", now)).Decision);

        // A later snapshot still carries it: not again — and not after a restart either.
        AddRule("r-1");
        agent.ApplyRemoteSnapshot(SyncMapper.FromDto(Fetch()));
        agent.Dispose();
        _agents.Remove(agent);
        var (restarted, afterRestart) = StartAgent();
        AddRule("r-2");
        restarted.ApplyRemoteSnapshot(SyncMapper.FromDto(Fetch()));

        Assert.Single(announced);
        Assert.Empty(afterRestart);
        Assert.Equal(_hub.WithHub((hub, _) => hub.Version), restarted.AgentSnapshotVersion);
    }

    /// <summary>A hub from before decisions travelled sends no list: the agent applies the settings and announces nothing.</summary>
    [Fact]
    public void A_snapshot_without_the_list_is_applied_and_announces_nothing()
    {
        var (agent, announced) = StartAgent();
        var id = Ask("laptop-yoav", "yoav", "https://www.instagram.com/");
        _hub.Decide(id, approve: true, ApproveScope.Host, ApprovalDuration.Always, null, "parent");
        var json = JsonNode.Parse(JsonSerializer.Serialize(Fetch(), HubApiJsonContext.Default.SnapshotDto))!.AsObject();
        Assert.True(json.Remove("decisions"));

        var older = JsonSerializer.Deserialize(json.ToJsonString(), HubApiJsonContext.Default.SnapshotDto)!;
        agent.ApplyRemoteSnapshot(SyncMapper.FromDto(older));

        Assert.Null(older.Decisions);
        Assert.Empty(announced);
        Assert.Equal(older.Version, agent.AgentSnapshotVersion);
    }

    [Fact]
    public void A_decision_this_version_cannot_read_is_left_out()
    {
        var at = _clock.Now;
        var snapshot = Fetch() with
        {
            Decisions =
            [
                new DecisionDto("ar-7", "yoav", "laptop-yoav", "website", "ynet.co.il", "approved", at),
                new DecisionDto("ar-8", "yoav", "laptop-yoav", "url", "ynet.co.il", "revoked", at),
                new DecisionDto("ar-9", "yoav", "laptop-yoav", "app", @"c:\games\minecraft.exe", "denied", at),
            ],
        };

        var decision = Assert.Single(SyncMapper.FromDto(snapshot).Decisions!);

        Assert.Equal(new RequestDecision("ar-9", "yoav", "laptop-yoav", RequestKind.App, @"c:\games\minecraft.exe", RequestStatus.Denied, at, null), decision);
    }

    /// <summary>A request from <paramref name="device"/>, as the hub API receives it; returns the id the hub gave it.</summary>
    private string Ask(string device, string member, string url)
    {
        var request = new RequestDto("pending", member, device, "url", url, HubCore.TargetKeyFor(RequestKind.Url, url, default), null, null, _clock.Now, "pending", null);
        var result = _hub.CreateRequestFromAgent(device, request);
        Assert.Equal("created", result.Result);
        return result.RequestId!;
    }

    private void AddRule(string id) =>
        _hub.WithHub((hub, now) => { hub.AddRule(new Rule(id, "yoav", $"{id}.example", PatternType.Domain, RuleAction.Block), now); return 0; }, "parent");

    /// <summary>The whole snapshot, through its JSON as it travels.</summary>
    private SnapshotDto Fetch() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(_hub.SnapshotSince(-1)!, HubApiJsonContext.Default.SnapshotDto), HubApiJsonContext.Default.SnapshotDto)!;

    private (FamilyService Agent, List<RequestDecision> Announced) StartAgent()
    {
        var agent = new FamilyService(Options.Create(new ServiceOptions
        {
            Role = "agent", DeviceId = "laptop-yoav", DataDirectory = Path.Combine(_dir, "agent"), ProfileDirectory = Path.Combine(_dir, "agent-profiles"),
        }), _clock, NullLogger<FamilyService>.Instance);
        agent.Start();
        _agents.Add(agent);
        var announced = new List<RequestDecision>();
        agent.RequestDecided += announced.Add;
        return (agent, announced);
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
}

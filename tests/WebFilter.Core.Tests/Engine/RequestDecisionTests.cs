using System.Text.Json.Nodes;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;

namespace WebFilter.Core.Tests.Engine;

/// <summary>
/// MVP-PLAN §8.1: a parent's decision reaches the member's browsers on every computer. The hub's snapshots for agents carry
/// the decisions of the last 48 hours, and an agent announces each new one of a member with an account on it, once.
/// </summary>
public class RequestDecisionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00");

    [Fact]
    public void The_snapshot_for_agents_carries_the_recent_decisions_newest_first_with_the_rules_expiry()
    {
        var hub = new HubCore(Fixture());
        var old = Request(hub, "yoav", "laptop-yoav", RequestKind.Url, "https://old.example/", Now.AddHours(-50));
        hub.Deny(old, Now.AddHours(-49));
        var site = Request(hub, "yoav", "laptop-yoav", RequestKind.Url, "https://www.ynet.co.il/news", Now.AddMinutes(-5));
        hub.Approve(site, ApproveScope.Host, ApprovalDuration.For(30), null, Now.AddMinutes(-1));
        var channel = Request(hub, "noa", "pc-kids", RequestKind.YouTube, "UCnatgeoKids000000000004", Now.AddMinutes(-3));
        hub.Approve(channel, ApproveScope.Host, ApprovalDuration.Always, null, Now);
        Request(hub, "noa", "pc-kids", RequestKind.Url, "https://pending.example/", Now);

        var decisions = hub.SnapshotForAgents(Now).Decisions!;

        Assert.Equal([channel, site], decisions.Select(d => d.RequestId));
        Assert.Equal(new RequestDecision(site, "yoav", "laptop-yoav", RequestKind.Url, "ynet.co.il", RequestStatus.Approved, Now.AddMinutes(-1), Now.AddMinutes(29)), decisions[1]);
        Assert.Equal((RequestKind.YouTube, (DateTimeOffset?)null), (decisions[0].Kind, decisions[0].Until));
        // 48 hours on, today's decisions are no longer sent.
        Assert.Empty(hub.SnapshotForAgents(Now.AddHours(48).AddMinutes(1)).Decisions!);
    }

    /// <summary>On the hub the service announces a decision as the parent makes it, so its own agent's snapshot has none.</summary>
    [Fact]
    public void The_hubs_own_snapshot_carries_no_decisions()
    {
        var hub = new HubCore(Fixture());
        hub.Approve(Request(hub, "noa", "pc-kids", RequestKind.Url, "https://pbskids.org/", Now), ApproveScope.Host, ApprovalDuration.Always, null, Now);
        var agent = new AgentCore("pc-parents");

        Assert.Null(hub.Snapshot().Decisions);
        Assert.Empty(agent.Apply(hub.Snapshot()));
    }

    [Fact]
    public void At_most_the_newest_hundred_decisions_travel()
    {
        var hub = new HubCore(Fixture());
        var start = Now.AddHours(-40);
        for (var i = 0; i < 120; i++)
        {
            var at = start.AddMinutes(15 * i); // 4 an hour: under the rate limit
            hub.Deny(Request(hub, "yoav", "laptop-yoav", RequestKind.Url, $"https://site{i}.example/", at), at);
        }

        var decisions = hub.SnapshotForAgents(start.AddMinutes(15 * 120)).Decisions!;

        Assert.Equal(AccessRequestRules.MaxDecisionsSent, decisions.Count);
        Assert.Equal("site119.example", decisions[0].TargetKey);
        Assert.Equal("site20.example", decisions[^1].TargetKey);
    }

    [Fact]
    public void An_agent_announces_a_decision_once()
    {
        var hub = new HubCore(Fixture());
        var agent = new AgentCore("laptop-yoav");
        Assert.Empty(agent.Apply(hub.SnapshotForAgents(Now)));
        var id = Request(hub, "yoav", "laptop-yoav", RequestKind.Url, "https://www.ynet.co.il/", Now);
        hub.Approve(id, ApproveScope.Host, ApprovalDuration.Always, null, Now);

        var announced = Assert.Single(agent.Apply(hub.SnapshotForAgents(Now)));

        Assert.Equal((id, "yoav", RequestStatus.Approved), (announced.RequestId, announced.MemberId, announced.Status));
        // Every later snapshot carries it for 48 hours: not again.
        Assert.Empty(agent.Apply(hub.SnapshotForAgents(Now.AddMinutes(1))));
        hub.AddRule(new Rule("r-later", "yoav", "chess.example", PatternType.Domain, RuleAction.Block), Now.AddMinutes(2));
        Assert.Empty(agent.Apply(hub.SnapshotForAgents(Now.AddMinutes(2))));
    }

    [Fact]
    public void Decisions_are_announced_on_every_computer_of_the_member_and_only_there()
    {
        var hub = new HubCore(Fixture());
        var kids = new AgentCore("pc-kids");
        var laptop = new AgentCore("laptop-yoav");
        // noa has an account on pc-kids only; yoav on pc-kids and laptop-yoav. Both asked on pc-kids.
        var noas = Request(hub, "noa", "pc-kids", RequestKind.Url, "https://pbskids.org/", Now);
        var yoavs = Request(hub, "yoav", "pc-kids", RequestKind.Url, "https://www.ynet.co.il/", Now);
        hub.Approve(noas, ApproveScope.Host, ApprovalDuration.Always, null, Now);
        hub.Deny(yoavs, Now.AddSeconds(1));

        Assert.Equal([noas, yoavs], kids.Apply(hub.SnapshotForAgents(Now)).Select(d => d.RequestId));
        Assert.Equal([yoavs], laptop.Apply(hub.SnapshotForAgents(Now)).Select(d => d.RequestId));
        Assert.Empty(new AgentCore("pc-parents").Apply(hub.SnapshotForAgents(Now)));
    }

    [Fact]
    public void A_request_decided_again_is_announced_again()
    {
        var hub = new HubCore(Fixture());
        var agent = new AgentCore("laptop-yoav");
        var id = Request(hub, "yoav", "laptop-yoav", RequestKind.Url, "https://www.ynet.co.il/", Now);
        hub.Approve(id, ApproveScope.Host, ApprovalDuration.For(15), null, Now);
        Assert.Single(agent.Apply(hub.SnapshotForAgents(Now)));

        hub.Deny(id, Now.AddMinutes(5));

        Assert.Equal(RequestStatus.Denied, Assert.Single(agent.Apply(hub.SnapshotForAgents(Now.AddMinutes(5)))).Status);
    }

    /// <summary>An older hub sends no list: nothing is announced, and what was seen stays seen.</summary>
    [Fact]
    public void A_snapshot_without_decisions_announces_nothing_and_forgets_nothing()
    {
        var hub = new HubCore(Fixture());
        var agent = new AgentCore("laptop-yoav");
        var id = Request(hub, "yoav", "laptop-yoav", RequestKind.Url, "https://www.ynet.co.il/", Now);
        hub.Approve(id, ApproveScope.Host, ApprovalDuration.Always, null, Now);
        Assert.Single(agent.Apply(hub.SnapshotForAgents(Now)));

        Assert.Empty(agent.Apply(hub.Snapshot()));

        Assert.True(agent.DecisionsSeen.ContainsKey(id));
        Assert.Empty(agent.Apply(hub.SnapshotForAgents(Now.AddMinutes(1))));
    }

    [Fact]
    public void What_was_announced_survives_a_restart_and_is_forgotten_once_the_hub_stops_sending_it()
    {
        var hub = new HubCore(Fixture());
        var agent = new AgentCore("laptop-yoav");
        var id = Request(hub, "yoav", "laptop-yoav", RequestKind.Url, "https://www.ynet.co.il/", Now);
        hub.Approve(id, ApproveScope.Host, ApprovalDuration.Always, null, Now);
        Assert.Single(agent.Apply(hub.SnapshotForAgents(Now)));

        var restarted = AgentCore.Restore(agent.Export());

        Assert.Equal(Now, restarted.DecisionsSeen[id]);
        Assert.Empty(restarted.Apply(hub.SnapshotForAgents(Now.AddMinutes(1))));
        Assert.Empty(restarted.Apply(hub.SnapshotForAgents(Now.AddHours(49))));
        Assert.Empty(restarted.DecisionsSeen);
    }

    private static string Request(HubCore hub, string member, string device, RequestKind kind, string target, DateTimeOffset at)
    {
        var draft = new AccessRequestDraft(member, device, kind, target, HubCore.TargetKeyFor(kind, target, DateOnly.FromDateTime(at.DateTime)), null, null, at);
        Assert.Equal(RequestResult.Created, hub.CreateRequest(draft, out var request));
        return request!.Id;
    }

    private static FamilyConfig Fixture() =>
        FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);
}

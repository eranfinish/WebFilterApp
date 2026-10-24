using System.Text.Json.Nodes;
using WebFilter.Core.Decisions;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;

namespace WebFilter.Core.Tests.Engine;

/// <summary>PROTECTION-SPEC §6 and §10 in the engine: search, override and tamper events, the hub's hooks and event retention.</summary>
public class ProtectionEventsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void The_new_reason_codes_have_their_wire_names()
    {
        Assert.Equal("blocked_image_search", ReasonCode.BlockedImageSearch.ToWire());
        Assert.Equal("blocked_jump", ReasonCode.BlockedJump.ToWire());
        Assert.Equal("blocked_youtube_shorts", ReasonCode.BlockedYouTubeShorts.ToWire());
        Assert.Equal(ReasonCode.BlockedJump, ReasonCodes.Parse("blocked_jump"));
    }

    [Fact]
    public void Searches_are_recorded_only_for_a_restricted_member_whose_searches_are_logged()
    {
        var config = Fixture();
        Flag(config, "noa", m => m with { LogSearches = true });
        Flag(config, "savta", m => m with { LogSearches = true }); // unrestricted: ignored
        var agent = Agent(config);

        Assert.True(agent.ReportSearch(agent.AccountOf("noa"), "google", "how do volcanoes work", "https://www.google.com/search?q=volcano", Now));
        Assert.False(agent.ReportSearch(agent.AccountOf("yoav"), "bing", "x", "https://www.bing.com/search?q=x", Now));
        Assert.False(agent.ReportSearch(agent.AccountOf("savta"), "google", "x", null, Now));
        Assert.False(agent.ReportSearch(agent.AccountNamed("visitor"), "google", "x", null, Now));

        var search = Assert.Single(agent.LocalEvents, e => e.Type == EventType.Search);
        Assert.Equal(("noa", "pc-kids", "google:how do volcanoes work", "https://www.google.com/search?q=volcano"),
            (search.MemberId, search.DeviceId, search.Detail, search.Url));
    }

    [Fact]
    public void Overrides_and_tamper_reach_the_hub_like_any_event()
    {
        var config = Fixture();
        var hub = new HubCore(config);
        var recorded = new List<FamilyEvent>();
        hub.EventRecorded += recorded.Add;
        var agent = new AgentCore("pc-kids");
        agent.Apply(hub.Snapshot());

        agent.ReportOverride(agent.AccountOf("yoav"), "https://other.example/", ReasonCode.BlockedJump, Now);
        agent.ReportTamper("S-1-5-21-2000-1001", TamperKinds.NetworkRepaired, Now); // noa's account
        agent.ReportTamper(null, TamperKinds.UncleanStop, Now);
        hub.Ingest(agent.TakeUpload());

        Assert.Equal([EventType.Override, EventType.Tamper, EventType.Tamper], recorded.Select(e => e.Type));
        Assert.Equal(("yoav", ReasonCode.BlockedJump, "https://other.example/"), (recorded[0].MemberId, recorded[0].Reason, recorded[0].Url));
        Assert.Equal(("noa", "network_repaired"), (recorded[1].MemberId, recorded[1].Detail));
        Assert.Equal((null, "pc-kids", "unclean_stop"), (recorded[2].MemberId, recorded[2].DeviceId, recorded[2].Detail));
    }

    /// <summary>PROTECTION-SPEC §14: a browser blocked for lack of its extension is a tamper event naming the browser and the member.</summary>
    [Fact]
    public void A_missing_extension_names_the_browser_after_the_kind()
    {
        Assert.Equal("extension_missing:chrome", TamperKinds.ExtensionMissingIn(BrowserKind.Chrome));
        Assert.Equal("extension_missing:edge", TamperKinds.ExtensionMissingIn(BrowserKind.Edge));
        Assert.StartsWith(TamperKinds.ExtensionMissing + ":", TamperKinds.ExtensionMissingIn(BrowserKind.Edge), StringComparison.Ordinal);

        var hub = new HubCore(Fixture());
        var agent = new AgentCore("pc-kids");
        agent.Apply(hub.Snapshot());
        agent.ReportTamper("S-1-5-21-2000-1001", TamperKinds.ExtensionMissingIn(BrowserKind.Chrome), Now); // noa's account
        var e = Assert.Single(agent.TakeUpload().Events);
        Assert.Equal((EventType.Tamper, "noa", "pc-kids", "extension_missing:chrome"), (e.Type, e.MemberId, e.DeviceId, e.Detail));
    }

    [Fact]
    public void A_tamper_before_the_first_settings_still_names_the_computer()
    {
        var agent = new AgentCore("laptop-new");
        agent.ReportTamper("S-1-5-21-9-9", TamperKinds.UncleanStop, Now);
        var e = Assert.Single(agent.TakeUpload().Events);
        Assert.Equal((EventType.Tamper, null, "laptop-new"), (e.Type, e.MemberId, e.DeviceId));
    }

    [Fact]
    public void The_hub_announces_new_requests_but_not_merged_ones()
    {
        var hub = new HubCore(Fixture());
        var accepted = new List<AccessRequest>();
        hub.RequestAccepted += accepted.Add;
        var draft = new AccessRequestDraft("noa", "pc-kids", RequestKind.Url, "https://pbskids.org/", "pbskids.org", null, null, Now);

        Assert.Equal(RequestResult.Created, hub.CreateRequest(draft, out _));
        Assert.Equal(RequestResult.Merged, hub.CreateRequest(draft, out _));

        Assert.Equal("pbskids.org", Assert.Single(accepted).TargetKey);
    }

    [Fact]
    public void Pruning_drops_only_the_leading_run_of_old_events()
    {
        var hub = new HubCore(Fixture());
        hub.Events.AddRange(
        [
            new FamilyEvent(EventType.Blocked, Now.AddDays(-100), "noa", null),
            new FamilyEvent(EventType.Blocked, Now.AddDays(-95), "noa", null),
            new FamilyEvent(EventType.Blocked, Now.AddDays(-1), "noa", null),
            new FamilyEvent(EventType.Blocked, Now.AddDays(-120), "noa", null), // late upload from an offline agent
        ]);

        Assert.Equal(2, hub.PruneEvents(Now.AddDays(-90)));
        Assert.Equal([Now.AddDays(-1), Now.AddDays(-120)], hub.Events.Select(e => e.At));
    }

    private static AgentCore Agent(FamilyConfig config)
    {
        var agent = new AgentCore("pc-kids");
        agent.Apply(new HubCore(config).Snapshot());
        return agent;
    }

    private static void Flag(FamilyConfig config, string id, Func<FamilyMember, FamilyMember> change)
    {
        var index = config.Members.FindIndex(m => m.Id == id);
        config.Members[index] = change(config.Members[index]);
    }

    private static FamilyConfig Fixture() =>
        FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);
}

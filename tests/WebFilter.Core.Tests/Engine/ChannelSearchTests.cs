using System.Text.Json.Nodes;
using WebFilter.Core.Decisions;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;
using WebFilter.Core.YouTube;

namespace WebFilter.Core.Tests.Engine;

/// <summary>
/// PROTECTION-SPEC §15: YouTube channel search for children in the "approved channels only" mode, and the channel names a
/// YouTube request carries to the parents and into the approved channel. The decisions themselves are scenarios (YT-008 to
/// YT-010, YTP-004, AR-011); these are the parts around them.
/// </summary>
public class ChannelSearchTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-01T16:00:00+02:00");
    private const string NatGeo = "UCnatgeoKids000000000004";

    [Fact]
    public void The_channel_search_url_is_youtubes_own_channels_filter()
    {
        var url = YouTubeUrlParser.ChannelSearchUrl(" space cats ");

        Assert.Equal("https://www.youtube.com/results?search_query=space%20cats&sp=EgIQAg%253D%253D", url);
        Assert.Equal(new YouTubeUrl(YouTubeUrlKind.Search, YouTubeSite.Www, SearchQuery: "space cats", ChannelsOnly: true), YouTubeUrlParser.Parse(url));
        // On the page's own origin (the Edge harness serves youtube.com on another port); Hebrew terms survive the round trip.
        var hebrew = YouTubeUrlParser.ChannelSearchUrl("דינוזאורים & חברים", "https://www.youtube.com:18443");
        Assert.StartsWith("https://www.youtube.com:18443/results?search_query=", hebrew);
        Assert.Equal("דינוזאורים & חברים", YouTubeUrlParser.Parse(hebrew).SearchQuery);
    }

    [Theory]
    [InlineData("https://www.youtube.com/results?search_query=+")]
    [InlineData("https://www.youtube.com/results?search_query=")]
    [InlineData("https://www.youtube.com/results?sp=EgIQAg%253D%253D")]
    public void A_search_without_terms_has_no_query(string url) => Assert.Null(YouTubeUrlParser.Parse(url).SearchQuery);

    [Theory]
    [InlineData("sp=EgIQAg%25253D%25253D", false)] // encoded three times: not what YouTube writes
    [InlineData("sp=", false)]
    [InlineData("sp", false)]
    [InlineData("SP=EgIQAg%253D%253D", false)] // parameter names are case-sensitive
    [InlineData("sp=EgIQAg", true)]
    public void Only_the_channels_filter_alone_is_channels_only(string filter, bool channelsOnly) =>
        Assert.Equal(channelsOnly, YouTubeUrlParser.Parse($"https://www.youtube.com/results?search_query=x&{filter}").ChannelsOnly);

    [Fact]
    public void The_switch_matters_only_in_the_approved_channels_mode()
    {
        var config = Fixture();
        Set(config, "noa", m => m with { YouTubeChannelSearch = true });
        Set(config, "yoav", m => m with { YouTubeChannelSearch = true });
        var view = new HubCore(config).View();
        DecisionResult Decide(string member, string url) => Decider.YouTube(view, member, new YouTubePage(url), Now);
        const string plain = "https://www.youtube.com/results?search_query=dinosaurs";

        Assert.Equal(DecisionResult.Redirect(ReasonCode.RedirectYouTubeChannelSearch), Decide("noa", plain));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedDefault), Decide("noa", YouTubeUrlParser.ChannelSearchUrl("dinosaurs")));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedDefault), Decide("yoav", plain)); // restricted mode: every search
        Set(config, "noa", m => m with { YouTubeMode = YouTubeMode.Off });
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedYouTubeOff), Decider.YouTube(new HubCore(config).View(), "noa", new YouTubePage(plain), Now));
    }

    [Fact]
    public void Features_carry_the_switch_for_restricted_members_only()
    {
        var config = Fixture();
        Set(config, "noa", m => m with { YouTubeChannelSearch = true });
        Set(config, "savta", m => m with { YouTubeChannelSearch = true });
        var hub = new HubCore(config);
        var accounts = hub.Config.Device("pc-kids").Accounts;
        ExtensionFeatures Of(string name) => ExtensionPolicy.For(hub.View(), accounts.Single(a => a.Name == name), Now).Features;

        Assert.True(Of("noa").YouTubeChannelSearch);
        Assert.False(Of("yoav").YouTubeChannelSearch);
        Assert.False(Of("savta").YouTubeChannelSearch);   // an adult: the baseline, which never searches for channels
        Assert.False(Of("visitor").YouTubeChannelSearch); // unlinked
    }

    [Theory]
    [InlineData("  NASA  ", "NASA")]
    [InlineData("Kids‮ TV\nnews\t ", "Kids TV news")] // a direction override is dropped, control characters become spaces
    [InlineData("👨‍👩‍👧 Family", "👨‍👩‍👧 Family")]             // the joiners of an emoji stay
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Titles_are_cleaned(string? input, string? expected) => Assert.Equal(expected, RequestNames.Title(input));

    [Fact]
    public void Long_names_are_cut()
    {
        Assert.Equal(RequestNames.MaxTitle, RequestNames.Title(new string('a', 300))!.Length);
        Assert.Equal(RequestNames.MaxSearchQuery, RequestNames.SearchQuery(new string('q', 300))!.Length);
    }

    [Theory]
    [InlineData("@NASA", "@nasa")]
    [InlineData("NatGeoKids-Sim", "@natgeokids-sim")]
    [InlineData("@ערוץ_הילדים", "@ערוץ_הילדים")]
    [InlineData("@a.b·c", "@a.b·c")]
    [InlineData("@with space", null)]
    [InlineData("@x/y", null)]
    [InlineData("@", null)]
    [InlineData("", null)]
    public void Handles_are_names_in_lower_case(string input, string? expected) => Assert.Equal(expected, RequestNames.Handle(input));

    [Fact]
    public void An_approved_channel_keeps_the_names_the_request_brought()
    {
        var hub = new HubCore(Fixture());
        var id = Request(hub, "noa", NatGeo, "NatGeo Kids (sim)", "@natgeokids-sim", "animals");

        hub.Approve(id, ApproveScope.Host, ApprovalDuration.Always, null, Now);

        var channel = Assert.Single(hub.Config.YouTubeChannels, c => c.ChannelId == NatGeo);
        Assert.Equal(new YouTubeChannel(NatGeo, "@natgeokids-sim", "NatGeo Kids (sim)", "noa", RuleAction.Allow), channel);
        Assert.Equal(channel, Assert.Single(hub.Snapshot().Config.YouTubeChannels, c => c.ChannelId == NatGeo));
    }

    [Fact]
    public void A_request_merged_into_an_open_one_adds_the_names_it_lacked()
    {
        var hub = new HubCore(Fixture());
        var id = Request(hub, "noa", NatGeo, null, null, null);
        var later = new AccessRequestDraft("noa", "pc-kids", RequestKind.YouTube, NatGeo, NatGeo, null, null, Now.AddMinutes(1), "NatGeo Kids (sim)", "@natgeokids-sim", "animals");

        Assert.Equal(RequestResult.Merged, hub.CreateRequest(later, out var merged));

        Assert.Equal((id, "NatGeo Kids (sim)", "@natgeokids-sim", "animals"), (merged!.Id, merged.Title, merged.Handle, merged.SearchQuery));
        // Names already known are not replaced by another browser's.
        hub.CreateRequest(later with { Title = "Other", CreatedAt = Now.AddMinutes(2) }, out merged);
        Assert.Equal("NatGeo Kids (sim)", merged!.Title);
    }

    [Fact]
    public void A_queued_request_keeps_its_names_on_the_way_to_the_hub()
    {
        var hub = new HubCore(Fixture());
        var agent = new AgentCore("pc-kids") { HubReachable = false };
        agent.Apply(hub.Snapshot());
        var draft = new AccessRequestDraft("noa", "pc-kids", RequestKind.YouTube, NatGeo, NatGeo, null, null, Now);
        Assert.Equal(RequestResult.Queued, agent.RequestAccess(draft, _ => RequestResult.Queued));
        Assert.Equal(RequestResult.Merged, agent.RequestAccess(draft with { Title = "NatGeo Kids (sim)", Handle = "@natgeokids-sim" }, _ => RequestResult.Queued));

        var restored = AgentCore.Restore(agent.Export());
        hub.Ingest(restored.TakeUpload());

        var request = Assert.Single(hub.Requests);
        Assert.Equal(("NatGeo Kids (sim)", "@natgeokids-sim"), (request.Title, request.Handle));
    }

    [Fact]
    public void Approving_a_channel_the_parents_blocked_for_the_member_allows_it_and_keeps_its_names()
    {
        var config = Fixture();
        config.YouTubeChannels.Add(new YouTubeChannel(NatGeo, "@natgeokids-sim", "NatGeo Kids (sim)", "noa", RuleAction.Block));
        var hub = new HubCore(config);
        var page = new YouTubePage("https://www.youtube.com/watch?v=vidNatg0001", NatGeo);
        Assert.Equal(ReasonCode.BlockedYouTubeChannel, Decider.YouTube(hub.View(), "noa", page, Now).Reason);

        hub.Approve(Request(hub, "noa", NatGeo, null, null, null), ApproveScope.Host, ApprovalDuration.Always, null, Now);

        Assert.Equal(new YouTubeChannel(NatGeo, "@natgeokids-sim", "NatGeo Kids (sim)", "noa", RuleAction.Allow),
            Assert.Single(hub.Config.YouTubeChannels, c => c.ChannelId == NatGeo));
        Assert.Equal(ReasonCode.AllowedYouTubeChannel, Decider.YouTube(hub.View(), "noa", page, Now).Reason);
    }

    private static string Request(HubCore hub, string member, string channelId, string? title, string? handle, string? query)
    {
        var draft = new AccessRequestDraft(member, "pc-kids", RequestKind.YouTube, channelId, channelId, null, null, Now, title, handle, query);
        Assert.Equal(RequestResult.Created, hub.CreateRequest(draft, out var request));
        return request!.Id;
    }

    private static void Set(FamilyConfig config, string memberId, Func<FamilyMember, FamilyMember> change)
    {
        var index = config.Members.FindIndex(m => m.Id == memberId);
        config.Members[index] = change(config.Members[index]);
    }

    private static FamilyConfig Fixture() =>
        FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);
}

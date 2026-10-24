using System.Text;
using System.Text.Json;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Agent;
using WebFilter.Contracts.Hub;

namespace WebFilter.Contracts.Tests;

/// <summary>
/// PROTECTION-SPEC §15 on the wire: the member switch youTubeChannelSearch, the feature in the rules reply, and the names a
/// YouTube request carries — all optional, so older services, agents and extensions keep working.
/// </summary>
public class ChannelSearchContractTests
{
    [Fact]
    public void The_member_switch_uses_the_spec_wire_name_and_is_off_when_missing()
    {
        var bundle = new FamilyBundle { TimeZone = "Asia/Jerusalem", Members = [new MemberDto("noa", "נועה", "child", 8, "allowlist", "allowlist", YouTubeChannelSearch: true)] };
        var json = FamilyBundleJson.Serialize(bundle);

        Assert.Contains("\"youTubeChannelSearch\":true", json);
        Assert.True(FamilyBundleJson.Deserialize(json).Members[0].YouTubeChannelSearch);
        var older = FamilyBundleJson.Deserialize("""{"timeZone":"Asia/Jerusalem","members":[{"id":"noa","displayName":"n","role":"child","filterMode":"allowlist","youTubeMode":"allowlist"}]}""");
        Assert.False(older.Members[0].YouTubeChannelSearch);

        var set = Encoding.UTF8.GetString(AdminProtocol.Serialize(new SetMember("noa", YouTubeChannelSearch: true)));
        Assert.Contains("\"youTubeChannelSearch\":true", set);
        Assert.DoesNotContain("blockAds", set);
        Assert.Null(Assert.IsType<SetMember>(AdminProtocol.Deserialize("""{"type":"setMember","v":1,"memberId":"noa","jumpGuard":true}"""u8)).YouTubeChannelSearch);
    }

    [Fact]
    public void The_rules_reply_carries_the_feature_and_an_older_one_reads_as_off()
    {
        var rules = Encoding.UTF8.GetString(AgentProtocol.Serialize(new RulesResponse(1, [], [], new YouTubePolicy("allowlist", [], []),
            new TimeStateMessage("ok", null, null), new AgentFeatures(YouTubeChannelSearch: true))));
        Assert.Contains("\"youTubeChannelSearch\":true", rules);

        var older = Assert.IsType<RulesResponse>(AgentProtocol.Deserialize(
            """{"type":"rules","v":1,"rulesVersion":3,"dynamicRules":[],"timeUpSessionRules":[],"youtube":{"mode":"allowlist","allowed":[],"blocked":[]},"time":{"type":"timeState","state":"ok"},"features":{"jumpGuard":true}}"""u8));
        Assert.False(older.Features!.YouTubeChannelSearch);
    }

    [Fact]
    public void A_youtube_request_may_name_its_channel_and_an_older_one_does_not()
    {
        var request = Assert.IsType<RequestAccessRequest>(AgentProtocol.Deserialize(
            """{"type":"requestAccess","v":1,"id":"7","kind":"youtube","target":"UCnatgeoKids000000000004","title":"NatGeo Kids","handle":"@natgeokids","searchQuery":"animals"}"""u8));
        Assert.Equal(("NatGeo Kids", "@natgeokids", "animals"), (request.Title, request.Handle, request.SearchQuery));

        var older = Assert.IsType<RequestAccessRequest>(AgentProtocol.Deserialize(
            """{"type":"requestAccess","v":1,"kind":"youtube","target":"UCnatgeoKids000000000004"}"""u8));
        Assert.Equal((null, null, null), (older.Title, older.Handle, older.SearchQuery));
        // Nothing new is written for a request without names, so an older service reads exactly what it read before.
        Assert.DoesNotContain("title", Encoding.UTF8.GetString(AgentProtocol.Serialize(new RequestAccessRequest("url", "https://a.example/", null, null))));
    }

    [Fact]
    public void Requests_carry_the_names_to_the_hub_and_the_admin_app()
    {
        var dto = new RequestDto("ar-1", "noa", "pc-kids", "youtube", "UCnatgeoKids000000000004", "UCnatgeoKids000000000004", null, null,
            DateTimeOffset.Parse("2026-11-01T16:00:00+02:00"), "pending", null, "NatGeo Kids", "@natgeokids", "animals");

        var hub = JsonSerializer.Serialize(new CreateRequestDto(dto), HubApi.Options);
        Assert.Contains("\"title\":\"NatGeo Kids\",\"handle\":\"@natgeokids\",\"searchQuery\":\"animals\"", hub);
        Assert.Equal(dto, JsonSerializer.Deserialize<CreateRequestDto>(hub, HubApi.Options)!.Request);
        var admin = Assert.IsType<RequestsResult>(AdminProtocol.Deserialize(AdminProtocol.Serialize(new RequestsResult([dto]))));
        Assert.Equal(dto, admin.Requests[0]);

        // An older agent's request has no names.
        var older = JsonSerializer.Deserialize<CreateRequestDto>(
            """{"request":{"id":"pending","memberId":"noa","deviceId":"pc-kids","kind":"youtube","target":"UCx","targetKey":"UCx","createdAt":"2026-11-01T16:00:00+02:00","status":"pending"}}""",
            HubApi.Options)!;
        Assert.Equal((null, null, null), (older.Request.Title, older.Request.Handle, older.Request.SearchQuery));
    }

    [Theory]
    [InlineData("NASA", "@nasa", "NASA (@nasa)")]
    [InlineData("NASA", null, "NASA")]
    [InlineData(null, "@nasa", "@nasa")]
    [InlineData("  ", " ", "UCLA_DiQTUM8_Bp7gWl1D0RQ")]
    [InlineData(null, null, "UCLA_DiQTUM8_Bp7gWl1D0RQ")]
    public void A_channel_is_named_by_its_title_and_handle_or_else_its_id(string? title, string? handle, string expected)
    {
        Assert.Equal(expected, YouTubeChannelLabel.Of("UCLA_DiQTUM8_Bp7gWl1D0RQ", title, handle));
        Assert.Equal(expected.StartsWith("UC", StringComparison.Ordinal) ? null : expected, YouTubeChannelLabel.Name(title, handle));
    }
}

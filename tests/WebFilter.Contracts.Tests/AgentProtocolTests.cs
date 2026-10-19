using System.Text;
using System.Text.Json;
using WebFilter.Contracts.Agent;
using WebFilter.Contracts.Framing;

namespace WebFilter.Contracts.Tests;

public class AgentProtocolTests
{
    public static TheoryData<AgentMessage> Messages() => new()
    {
        new HelloRequest("0.2.0", "edge") { Id = "1" },
        new HelloResponse(new AgentUser("S-1-5-21-1", "noa", "child", false), "allowlist", "service", true, 7, "2.0.0") { Id = "1" },
        new GetRulesRequest(6),
        new RulesResponse(7, [JsonDocument.Parse("""{"id":1,"priority":1}""").RootElement], [],
            new YouTubePolicy("allowlist", [new YouTubeChannelRef("UCkhanKids00000000000001", "@khankids-sim", "Khan Kids")], []),
            new TimeStateMessage("ok", 45, "19:00")),
        new HeartbeatRequest(true, "active", true),
        new TimeStateMessage("warn", 5, null),
        new ReportBlockRequest("https://www.instagram.com/", "blocked_category", null),
        new ReportBlockResult("block", "blocked_rule", "r-1", null, true, "url"),
        new DecideUrlRequest("https://www.instagram.com/"),
        new DecideUrlResponse("block", "blocked_category", null, "social", true, "url"),
        new RequestAccessRequest("url", "https://pbskids.org/games", null, "homework"),
        new RequestAccessResponse("created", "ar-1"),
        new ResolveYouTubeRequest("@sesame-sim", null),
        new ResolveYouTubeResponse("UCsesame0000000000000002"),
        new RulesChangedMessage(8),
        new AccessDecidedMessage("ar-1", "approved", DateTimeOffset.Parse("2026-11-01T16:20:00+02:00")),
        new ErrorMessage("service_unavailable", "The filtering service is not running"),
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void Every_message_round_trips(AgentMessage message)
    {
        var json = AgentProtocol.Serialize(message);
        var back = AgentProtocol.Deserialize(json);
        Assert.Equal(message.GetType(), back.GetType());
        Assert.Equal(Encoding.UTF8.GetString(json), Encoding.UTF8.GetString(AgentProtocol.Serialize(back)));
    }

    [Fact]
    public void Wire_format_is_camel_case_with_version_and_type_and_omits_nulls()
    {
        var json = Encoding.UTF8.GetString(AgentProtocol.Serialize(new ReportBlockRequest("https://x.example/", "blocked_rule", null) { Id = "9" }));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("reportBlock", root.GetProperty("type").GetString());
        Assert.Equal(AgentMessage.CurrentVersion, root.GetProperty("v").GetInt32());
        Assert.Equal("9", root.GetProperty("id").GetString());
        Assert.Equal("blocked_rule", root.GetProperty("reason").GetString());
        Assert.False(root.TryGetProperty("ruleId", out _));
    }

    [Fact]
    public void Extension_style_json_is_accepted()
    {
        var message = AgentProtocol.Deserialize("""{"type":"heartbeat","v":1,"focused":true,"idle":"active","hasWebTab":false}"""u8);
        var heartbeat = Assert.IsType<HeartbeatRequest>(message);
        Assert.True(heartbeat.Focused);
        Assert.False(heartbeat.HasWebTab);
    }

    [Fact]
    public void Unknown_type_is_rejected() =>
        Assert.ThrowsAny<JsonException>(() => AgentProtocol.Deserialize("""{"type":"format-disk","v":1}"""u8));

    [Fact]
    public async Task Frames_round_trip_over_a_stream()
    {
        using var stream = new MemoryStream();
        await AgentProtocol.WriteAsync(stream, new HelloRequest("0.2.0", "chrome"));
        await AgentProtocol.WriteAsync(stream, new GetRulesRequest());
        stream.Position = 0;
        Assert.IsType<HelloRequest>(await AgentProtocol.ReadAsync(stream));
        Assert.IsType<GetRulesRequest>(await AgentProtocol.ReadAsync(stream));
        Assert.Null(await AgentProtocol.ReadAsync(stream));
    }

    [Fact]
    public async Task Truncated_frame_throws()
    {
        using var stream = new MemoryStream();
        await MessageFraming.WriteAsync(stream, Encoding.UTF8.GetBytes("""{"type":"getRules","v":1}"""));
        stream.SetLength(stream.Length - 3);
        stream.Position = 0;
        await Assert.ThrowsAsync<EndOfStreamException>(() => MessageFraming.ReadAsync(stream));
    }

    [Fact]
    public async Task Oversized_length_is_rejected()
    {
        using var stream = new MemoryStream(BitConverter.GetBytes(MessageFraming.MaxFrame + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => MessageFraming.ReadAsync(stream));
    }

    [Theory]
    [InlineData("""{"type":"somethingNewer","v":2,"id":"q7"}""", "somethingNewer", "q7")]
    [InlineData("""{"v":1,"id":"q8"}""", null, "q8")]
    [InlineData("""not json""", null, null)]
    public async Task Lenient_reading_turns_unknown_frames_into_UnknownMessage(string json, string? type, string? id)
    {
        using var stream = new MemoryStream();
        await MessageFraming.WriteAsync(stream, Encoding.UTF8.GetBytes(json));
        await AgentProtocol.WriteAsync(stream, new HeartbeatRequest(true, "active", true));
        stream.Position = 0;

        var unknown = Assert.IsType<UnknownMessage>(await AgentProtocol.ReadLenientAsync(stream));
        Assert.Equal((type, id), (unknown.Type, unknown.Id));
        Assert.IsType<HeartbeatRequest>(await AgentProtocol.ReadLenientAsync(stream));
    }
}

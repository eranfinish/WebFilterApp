using WebFilter.Core.Tests.Urls;
using WebFilter.Core.YouTube;

namespace WebFilter.Core.Tests.YouTube;

public class YouTubeUrlParserTests
{
    public static TheoryData<string, string, string> ParseCases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var scenario in ScenarioData.Scenarios("youtube-parse.json"))
        {
            var id = scenario.GetProperty("id").GetString()!;
            foreach (var step in scenario.GetProperty("steps").EnumerateArray())
            {
                if (step.GetProperty("do").GetString() != "parseYouTube")
                    continue;
                foreach (var c in step.GetProperty("cases").EnumerateArray())
                    data.Add(id, c.GetProperty("url").GetString()!, c.GetProperty("expect").GetRawText());
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ParseCases))]
    public void Parse_MatchesScenario(string scenarioId, string url, string expectJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(expectJson);
        var e = doc.RootElement;
        var expected = new YouTubeUrl(
            ScenarioData.ParseEnum<YouTubeUrlKind>(e.GetProperty("kind").GetString()!),
            ScenarioData.OptString(e, "site") is { } site ? ScenarioData.ParseEnum<YouTubeSite>(site) : null,
            ScenarioData.OptString(e, "channelId"),
            ScenarioData.OptString(e, "handle"),
            ScenarioData.OptString(e, "customName"),
            ScenarioData.OptString(e, "userName"),
            ScenarioData.OptString(e, "videoId"),
            ScenarioData.OptString(e, "playlistId"),
            ScenarioData.OptString(e, "searchQuery"),
            e.TryGetProperty("channelsOnly", out var channelsOnly) && channelsOnly.GetBoolean());

        var actual = YouTubeUrlParser.Parse(url);
        // A search case that does not name its terms or filter (YTP-003) checks the kind and site only.
        if (!e.TryGetProperty("searchQuery", out _)) expected = expected with { SearchQuery = actual.SearchQuery };
        if (!e.TryGetProperty("channelsOnly", out _)) expected = expected with { ChannelsOnly = actual.ChannelsOnly };

        Assert.True(expected == actual, $"{scenarioId} {url}: expected {expected}, got {actual}");
    }

    [Theory]
    [InlineData("youtube.com")]
    [InlineData("www.youtube.com")]
    [InlineData("m.youtube.com")]
    [InlineData("music.youtube.com")]
    [InlineData("studio.youtube.com")]
    [InlineData("youtu.be")]
    [InlineData("youtube-nocookie.com")]
    [InlineData("www.youtube-nocookie.com")]
    [InlineData("WWW.YouTube.com")]
    public void IsYouTubeHost_True(string host) => Assert.True(YouTubeUrlParser.IsYouTubeHost(host));

    [Theory]
    [InlineData("notyoutube.com")]
    [InlineData("youtube.com.example.net")]
    [InlineData("youtube.co")]
    [InlineData("example.com")]
    [InlineData("youtu.be.example.com")]
    [InlineData("ytimg.com")]
    public void IsYouTubeHost_False(string host) => Assert.False(YouTubeUrlParser.IsYouTubeHost(host));

    [Fact]
    public void YouTubeOffScenarioHosts_AreYouTubeHosts()
    {
        // YT-005: every URL it lists is governed by the YouTube mode, not URL rules.
        var urls = ScenarioData.Scenario("youtube-decisions.json", "YT-005").GetProperty("steps")[0].GetProperty("cases")
            .EnumerateArray().Select(c => c.GetProperty("url").GetString()!);
        foreach (var url in urls)
        {
            Assert.True(Core.Urls.UrlNormalizer.TryNormalize(url, out var normalized));
            Assert.True(YouTubeUrlParser.IsYouTubeHost(normalized.Host), url);
        }
    }

    [Fact]
    public void Handle_IsPercentDecodedAndLowercased()
    {
        var parsed = YouTubeUrlParser.Parse("https://www.youtube.com/@%D7%A9%D7%9C%D7%95%D7%9D-Kids/videos");
        Assert.Equal(new YouTubeUrl(YouTubeUrlKind.Handle, YouTubeSite.Www, Handle: "@שלום-kids"), parsed);
    }

    [Theory]
    [InlineData("https://www.youtube.com/embed/videoseries?list=PLsim0001", YouTubeUrlKind.Embed, null, "PLsim0001")]
    [InlineData("https://www.youtube.com/live/vidLive0001?si=x", YouTubeUrlKind.Video, "vidLive0001", null)]
    [InlineData("https://www.youtube.com/watch?list=PLsim0001", YouTubeUrlKind.Playlist, null, "PLsim0001")]
    [InlineData("https://www.youtube.com/WATCH?v=vidKhan0001", YouTubeUrlKind.Video, "vidKhan0001", null)]
    [InlineData("https://www.youtube.com/watch", YouTubeUrlKind.Other, null, null)]
    [InlineData("https://www.youtube.com/shorts/", YouTubeUrlKind.Other, null, null)]
    [InlineData("https://youtu.be/vidKhan0001?list=PLsim0001", YouTubeUrlKind.Video, "vidKhan0001", "PLsim0001")]
    public void VideoAndPlaylistForms(string url, YouTubeUrlKind kind, string? videoId, string? playlistId)
    {
        var parsed = YouTubeUrlParser.Parse(url);
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(videoId, parsed.VideoId);
        Assert.Equal(playlistId, parsed.PlaylistId);
    }

    [Theory]
    [InlineData("https://YOUTUBE.COM./channel/UCkhanKids00000000000001", YouTubeSite.Www)]
    [InlineData("https://user@m.youtube.com:443/channel/UCkhanKids00000000000001#t", YouTubeSite.M)]
    public void HostIsNormalizedBeforeMatching(string url, YouTubeSite site) =>
        Assert.Equal(new YouTubeUrl(YouTubeUrlKind.Channel, site, ChannelId: "UCkhanKids00000000000001"), YouTubeUrlParser.Parse(url));

    [Theory]
    [InlineData("chrome://settings/")]
    [InlineData("not a url")]
    [InlineData("https://youtube.com@example.com/watch?v=vidKhan0001")]
    [InlineData("http://192.168.1.1/watch?v=vidKhan0001")]
    public void NonYouTube(string url) => Assert.Equal(YouTubeUrl.NotYouTube, YouTubeUrlParser.Parse(url));
}

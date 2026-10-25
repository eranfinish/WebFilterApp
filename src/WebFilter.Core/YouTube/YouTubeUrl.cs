namespace WebFilter.Core.YouTube;

public enum YouTubeUrlKind { Channel, Handle, Custom, User, Video, Shorts, Embed, Playlist, Home, Search, Other, NotYouTube }

/// <summary>Www covers youtube.com with or without www; Short is youtu.be.</summary>
public enum YouTubeSite { Www, M, Music, NoCookie, Short }

/// <summary>
/// A parsed YouTube URL (youtube-parse.json). Handles are lowercase with the leading '@'; custom and user names lowercase.
/// <see cref="Site"/> is null for <see cref="YouTubeUrlKind.NotYouTube"/>.
/// </summary>
/// <param name="SearchQuery">Search pages: the terms searched for (search_query, or q on YouTube Music), trimmed; null when none.</param>
/// <param name="ChannelsOnly">
/// Search pages: the results are restricted to channels — YouTube's "Type: Channels" filter is the only filter
/// (<see cref="YouTubeUrlParser.ChannelFilter"/>, PROTECTION-SPEC §15).
/// </param>
public sealed record YouTubeUrl(
    YouTubeUrlKind Kind,
    YouTubeSite? Site = null,
    string? ChannelId = null,
    string? Handle = null,
    string? CustomName = null,
    string? UserName = null,
    string? VideoId = null,
    string? PlaylistId = null,
    string? SearchQuery = null,
    bool ChannelsOnly = false)
{
    public static readonly YouTubeUrl NotYouTube = new(YouTubeUrlKind.NotYouTube);
}

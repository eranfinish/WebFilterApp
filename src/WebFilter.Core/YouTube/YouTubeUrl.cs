namespace WebFilter.Core.YouTube;

public enum YouTubeUrlKind { Channel, Handle, Custom, User, Video, Shorts, Embed, Playlist, Home, Search, Other, NotYouTube }

/// <summary>Www covers youtube.com with or without www; Short is youtu.be.</summary>
public enum YouTubeSite { Www, M, Music, NoCookie, Short }

/// <summary>
/// A parsed YouTube URL (youtube-parse.json). Handles are lowercase with the leading '@'; custom and user names lowercase.
/// <see cref="Site"/> is null for <see cref="YouTubeUrlKind.NotYouTube"/>.
/// </summary>
public sealed record YouTubeUrl(
    YouTubeUrlKind Kind,
    YouTubeSite? Site = null,
    string? ChannelId = null,
    string? Handle = null,
    string? CustomName = null,
    string? UserName = null,
    string? VideoId = null,
    string? PlaylistId = null)
{
    public static readonly YouTubeUrl NotYouTube = new(YouTubeUrlKind.NotYouTube);
}

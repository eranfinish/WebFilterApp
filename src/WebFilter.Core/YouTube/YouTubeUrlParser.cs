using WebFilter.Core.Urls;

namespace WebFilter.Core.YouTube;

public static class YouTubeUrlParser
{
    /// <summary>
    /// Parses any URL. Recognized hosts: youtube.com and its subdomains (www, m, music), youtu.be,
    /// youtube-nocookie.com and its subdomains. Look-alikes (youtube.com.example.net, notyoutube.com) are NotYouTube.
    /// </summary>
    /// <remarks>
    /// Other youtube.com subdomains count as <see cref="YouTubeSite.Www"/> so they stay under the YouTube mode.
    /// Besides the documented forms, <c>/live/{id}</c> is a video and <c>/embed/videoseries?list=</c> an embed with only a playlist.
    /// Path keywords are matched case-insensitively; ids keep their case.
    /// </remarks>
    public static YouTubeUrl Parse(string url)
    {
        if (!UrlNormalizer.TryNormalize(url, out var normalized) || SiteOf(normalized.Host) is not { } site)
            return YouTubeUrl.NotYouTube;

        var segments = normalized.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = normalized.Query;
        var playlistId = QueryValue(query, "list");

        if (site == YouTubeSite.Short)
        {
            return segments.Length == 0
                ? new YouTubeUrl(YouTubeUrlKind.Home, site)
                : new YouTubeUrl(YouTubeUrlKind.Video, site, VideoId: Unescape(segments[0]), PlaylistId: playlistId);
        }

        if (segments.Length == 0)
            return new YouTubeUrl(YouTubeUrlKind.Home, site);

        var first = Unescape(segments[0]);
        var second = segments.Length > 1 ? Unescape(segments[1]) : null;

        if (first.Length > 1 && first[0] == '@')
            return new YouTubeUrl(YouTubeUrlKind.Handle, site, Handle: first.ToLowerInvariant());

        switch (first.ToLowerInvariant())
        {
            case "watch":
                var videoId = QueryValue(query, "v");
                if (videoId is not null)
                    return new YouTubeUrl(YouTubeUrlKind.Video, site, VideoId: videoId, PlaylistId: playlistId);
                return playlistId is not null
                    ? new YouTubeUrl(YouTubeUrlKind.Playlist, site, PlaylistId: playlistId)
                    : new YouTubeUrl(YouTubeUrlKind.Other, site);

            case "live" when second is not null:
                return new YouTubeUrl(YouTubeUrlKind.Video, site, VideoId: second, PlaylistId: playlistId);

            case "shorts" when second is not null:
                return new YouTubeUrl(YouTubeUrlKind.Shorts, site, VideoId: second);

            case "embed":
                var embedded = second is null || second.Equals("videoseries", StringComparison.OrdinalIgnoreCase) ? null : second;
                return new YouTubeUrl(YouTubeUrlKind.Embed, site, VideoId: embedded, PlaylistId: playlistId);

            case "playlist" when playlistId is not null:
                return new YouTubeUrl(YouTubeUrlKind.Playlist, site, PlaylistId: playlistId);

            case "results" or "search":
                return new YouTubeUrl(YouTubeUrlKind.Search, site);

            case "channel" when second is not null:
                return new YouTubeUrl(YouTubeUrlKind.Channel, site, ChannelId: second);

            case "c" when second is not null:
                return new YouTubeUrl(YouTubeUrlKind.Custom, site, CustomName: second.ToLowerInvariant());

            case "user" when second is not null:
                return new YouTubeUrl(YouTubeUrlKind.User, site, UserName: second.ToLowerInvariant());

            default:
                return new YouTubeUrl(YouTubeUrlKind.Other, site);
        }
    }

    /// <summary>True for every host whose access is governed by the member's YouTube mode rather than URL rules.</summary>
    public static bool IsYouTubeHost(string normalizedHost) => normalizedHost is not null && SiteOf(normalizedHost) is not null;

    private static YouTubeSite? SiteOf(string host)
    {
        var h = host.AsSpan();
        if (h.EndsWith("."))
            h = h[..^1];

        if (IsDomainOrSubdomain(h, "youtube.com"))
        {
            if (h.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase))
                return YouTubeSite.M;
            if (h.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase))
                return YouTubeSite.Music;
            return YouTubeSite.Www;
        }
        if (IsDomainOrSubdomain(h, "youtube-nocookie.com"))
            return YouTubeSite.NoCookie;
        if (IsDomainOrSubdomain(h, "youtu.be"))
            return YouTubeSite.Short;
        return null;
    }

    private static bool IsDomainOrSubdomain(ReadOnlySpan<char> host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || (host.Length > domain.Length + 1
            && host[host.Length - domain.Length - 1] == '.'
            && host.EndsWith(domain, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first non-empty value of a query parameter, form-decoded.</summary>
    private static string? QueryValue(string query, string name)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq < 0 ? pair : pair[..eq];
            if (FormDecode(key) != name)
                continue;
            var value = eq < 0 ? "" : FormDecode(pair[(eq + 1)..]);
            if (value.Length > 0)
                return value;
        }
        return null;
    }

    private static string FormDecode(string s) => Unescape(s.Replace('+', ' '));

    private static string Unescape(string s) => s.Contains('%') ? Uri.UnescapeDataString(s) : s;
}

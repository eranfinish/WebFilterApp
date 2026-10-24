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
                var terms = (QueryValue(query, "search_query") ?? QueryValue(query, "q"))?.Trim();
                return new YouTubeUrl(YouTubeUrlKind.Search, site, SearchQuery: string.IsNullOrEmpty(terms) ? null : terms, ChannelsOnly: IsChannelFilter(query));

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

    /// <summary>
    /// YouTube's search filter "Type: Channels" (the <c>sp</c> parameter, base64 of the filter message: filters { type:
    /// channel }). Checked on youtube.com on 2026-10-25: the filter's own link is <c>sp=EgIQAg%253D%253D</c>.
    /// </summary>
    public const string ChannelFilter = "EgIQAg==";

    /// <summary><see cref="ChannelFilter"/> as YouTube's own "Channels" link writes it in a URL (encoded twice).</summary>
    public const string ChannelFilterParameter = "EgIQAg%253D%253D";

    public const string DefaultOrigin = "https://www.youtube.com";

    /// <summary>
    /// YouTube's results for <paramref name="query"/> restricted to channels (PROTECTION-SPEC §15), on
    /// <paramref name="origin"/> (scheme, host and port of a youtube.com page).
    /// </summary>
    public static string ChannelSearchUrl(string query, string origin = DefaultOrigin)
    {
        ArgumentNullException.ThrowIfNull(query);
        return $"{origin}/results?search_query={Uri.EscapeDataString(query.Trim())}&sp={ChannelFilterParameter}";
    }

    /// <summary>True for every host whose access is governed by the member's YouTube mode rather than URL rules.</summary>
    public static bool IsYouTubeHost(string normalizedHost) => normalizedHost is not null && SiteOf(normalizedHost) is not null;

    /// <summary>
    /// Google's sign-in passes through this host to set YouTube's cookies; it serves no content. It is a YouTube host, but
    /// the YouTube mode never blocks it (Off and homework included), so YouTube off does not break Google sign-in
    /// (PROTECTION-SPEC §11). The DNR compiler and the engine treat it the same way.
    /// </summary>
    public const string SignInHost = "accounts.youtube.com";

    /// <summary>True for <see cref="SignInHost"/> (and, as for DNR's requestDomains, its subdomains).</summary>
    public static bool IsSignInHost(string normalizedHost)
    {
        if (normalizedHost is null) return false;
        var h = normalizedHost.AsSpan();
        if (h.EndsWith(".")) h = h[..^1];
        return IsDomainOrSubdomain(h, SignInHost);
    }

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

    /// <summary>
    /// Whether the only <c>sp</c> parameter is <see cref="ChannelFilter"/>: encoded once or twice (YouTube's own links), with
    /// or without the padding. Another filter, a sort order, or a second <c>sp</c> is not channels only.
    /// </summary>
    private static bool IsChannelFilter(string query)
    {
        var values = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(kv => FormDecode(kv[0]) == "sp")
            .Select(kv => kv.Length > 1 ? FormDecode(kv[1]) : "")
            .ToList();
        if (values.Count != 1) return false;
        var value = values[0];
        for (var i = 0; i < 2; i++)
        {
            if (value is ChannelFilter or "EgIQAg") return true;
            var decoded = Unescape(value);
            if (decoded == value) return false;
            value = decoded;
        }
        return false;
    }

    private static string FormDecode(string s) => Unescape(s.Replace('+', ' '));

    private static string Unescape(string s) => s.Contains('%') ? Uri.UnescapeDataString(s) : s;
}

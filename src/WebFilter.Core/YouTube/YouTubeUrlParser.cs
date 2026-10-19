namespace WebFilter.Core.YouTube;

public static class YouTubeUrlParser
{
    /// <summary>
    /// Parses any URL. Recognized hosts: youtube.com and its subdomains (www, m, music), youtu.be,
    /// youtube-nocookie.com and its subdomains. Look-alikes (youtube.com.example.net, notyoutube.com) are NotYouTube.
    /// </summary>
    public static YouTubeUrl Parse(string url) => throw new NotImplementedException();

    /// <summary>True for every host whose access is governed by the member's YouTube mode rather than URL rules.</summary>
    public static bool IsYouTubeHost(string normalizedHost) => throw new NotImplementedException();
}

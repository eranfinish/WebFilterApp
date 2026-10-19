using WebFilter.Core.Urls;

namespace WebFilter.Core.Tests.Urls;

public class UrlNormalizerTests
{
    [Theory]
    // host case, trailing dot, ports, userinfo, fragment
    [InlineData("https://Example.COM/", "https://example.com/")]
    [InlineData("https://example.com./a", "https://example.com/a")]
    [InlineData("http://example.com:80/", "http://example.com/")]
    [InlineData("https://example.com:443/", "https://example.com/")]
    [InlineData("http://example.com:443/", "http://example.com:443/")]
    [InlineData("https://example.com:0080/", "https://example.com:80/")]
    [InlineData("https://example.com:/", "https://example.com/")]
    [InlineData("https://user:pa:ss@example.com/", "https://example.com/")]
    [InlineData("https://a@b@example.com/", "https://example.com/")]
    [InlineData("https://example.com/a#frag", "https://example.com/a")]
    [InlineData("https://example.com/a?q=1#frag?x", "https://example.com/a?q=1")]
    [InlineData("https://example.com/a#x?q=1", "https://example.com/a")]
    // empty path, dot segments, backslashes, missing slashes
    [InlineData("https://example.com", "https://example.com/")]
    [InlineData("https://example.com?q", "https://example.com/?q")]
    [InlineData("https://example.com/a/./b/../c", "https://example.com/a/c")]
    [InlineData("https://example.com/a/b/..", "https://example.com/a/")]
    [InlineData("https://example.com/a/.", "https://example.com/a/")]
    [InlineData("https://example.com/a/%2e%2E/b", "https://example.com/b")]
    [InlineData("https://example.com/a/.%2e", "https://example.com/")]
    [InlineData("https://example.com/../../x", "https://example.com/x")]
    [InlineData("https://example.com/a//b", "https://example.com/a//b")]
    [InlineData("https://example.com\\a\\b", "https://example.com/a/b")]
    [InlineData("https:\\\\example.com/", "https://example.com/")]
    [InlineData("https:example.com/x", "https://example.com/x")]
    [InlineData("https:////example.com/x", "https://example.com/x")]
    // scheme case, surrounding whitespace, embedded tabs/newlines
    [InlineData("HTTPS://example.com/", "https://example.com/")]
    [InlineData("  https://example.com/\t ", "https://example.com/")]
    [InlineData("https://exa\nmple.com/p\tq", "https://example.com/pq")]
    // path escapes are kept; raw characters a browser escapes are escaped
    [InlineData("https://example.com/a%2Fb/%41", "https://example.com/a%2Fb/%41")]
    [InlineData("https://example.com/a b/ש", "https://example.com/a%20b/%D7%A9")]
    [InlineData("https://example.com/?q=a b&x='", "https://example.com/?q=a%20b&x=%27")]
    // host percent-decoding and IDN
    [InlineData("https://ex%61mple.com/", "https://example.com/")]
    [InlineData("https://bücher.example/", "https://xn--bcher-kva.example/")]
    [InlineData("https://BÜCHER.example/", "https://xn--bcher-kva.example/")]
    [InlineData("https://b%C3%BCcher.example/", "https://xn--bcher-kva.example/")]
    [InlineData("https://XN--BCHER-KVA.example/", "https://xn--bcher-kva.example/")]
    [InlineData("https://ＥＸＡＭＰＬＥ．com/", "https://example.com/")]
    [InlineData("https://r3---sn-abc.googlevideo.com/videoplayback", "https://r3---sn-abc.googlevideo.com/videoplayback")]
    [InlineData("https://under_score.example/", "https://under_score.example/")]
    public void Normalize_ProducesCanonicalHref(string input, string expected)
    {
        Assert.True(UrlNormalizer.TryNormalize(input, out var url), input);
        Assert.Equal(expected, url.Href);
        Assert.Equal(HostKind.Domain, url.HostKind);
        Assert.False(url.IsIpLiteral);
    }

    [Theory]
    [InlineData("http://192.168.1.1/", "192.168.1.1")]
    [InlineData("http://3232235777/", "192.168.1.1")]
    [InlineData("http://0xC0A80101/", "192.168.1.1")]
    [InlineData("http://0xc0.0xa8.1.1/", "192.168.1.1")]
    [InlineData("http://0300.0250.01.01/", "192.168.1.1")]
    [InlineData("http://192.168.257/", "192.168.1.1")]
    [InlineData("http://192.11010305/", "192.168.1.1")]
    [InlineData("http://127.1/", "127.0.0.1")]
    [InlineData("http://0x7f.1/", "127.0.0.1")]
    [InlineData("http://0x/", "0.0.0.0")]
    [InlineData("http://192.168.1.1./", "192.168.1.1")]
    [InlineData("http://%31%39%32.168.1.1/", "192.168.1.1")]
    [InlineData("http://１９２.168.1.1/", "192.168.1.1")]
    [InlineData("http://user@3232235826:8080/x", "192.168.1.50")]
    public void Normalize_IPv4InEveryNotation(string input, string expectedHost)
    {
        Assert.True(UrlNormalizer.TryNormalize(input, out var url), input);
        Assert.Equal(expectedHost, url.Host);
        Assert.Equal(HostKind.IPv4, url.HostKind);
        Assert.True(url.IsIpLiteral);
    }

    [Theory]
    [InlineData("http://[::1]/", "::1")]
    [InlineData("http://[0:0:0:0:0:0:0:1]/", "::1")]
    [InlineData("http://[2001:DB8:0:0:0:0:0:1]/", "2001:db8::1")]
    [InlineData("http://[2001:db8:0:0:1:0:0:1]/", "2001:db8::1:0:0:1")]
    [InlineData("http://[1:0:0:2:0:0:0:3]/", "1:0:0:2::3")]
    [InlineData("http://[2001:db8:1:1:1:1:0:1]/", "2001:db8:1:1:1:1:0:1")]
    [InlineData("http://[0:0:0:0:0:0:0:0]/", "::")]
    [InlineData("http://[::ffff:192.168.1.1]/", "::ffff:c0a8:101")]
    [InlineData("http://[0001:00ab::]/", "1:ab::")]
    public void Normalize_IPv6IsCompressedWithoutBrackets(string input, string expectedHost)
    {
        Assert.True(UrlNormalizer.TryNormalize(input, out var url), input);
        Assert.Equal(expectedHost, url.Host);
        Assert.Equal(HostKind.IPv6, url.HostKind);
    }

    [Fact]
    public void Href_KeepsNonDefaultPortAndBracketsIPv6()
    {
        Assert.True(UrlNormalizer.TryNormalize("http://[::1]:8080/a?b", out var url));
        Assert.Equal(8080, url.Port);
        Assert.Equal("/a", url.Path);
        Assert.Equal("b", url.Query);
        Assert.Equal("http://[::1]:8080/a?b", url.Href);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example.com/path")]
    [InlineData("https://")]
    [InlineData("https:///")]
    [InlineData("https://user@/")]
    [InlineData("https://exa mple.com/")]
    [InlineData("https://exa<mple.com/")]
    [InlineData("https://exa%20mple.com/")]
    [InlineData("https://a%2Fb.example/")]
    [InlineData("https://ex%zzample.com/")]
    [InlineData("https://example.com:99999/")]
    [InlineData("https://example.com:8a/")]
    [InlineData("https://example.com:80:80/")]
    [InlineData("https://256.256.256.256/")]
    [InlineData("https://1.2.3.4.5/")]
    [InlineData("https://4294967296/")]
    [InlineData("https://0x100000000/")]
    [InlineData("https://1.2.3.08/")]
    [InlineData("https://example.1/")]
    [InlineData("https://[::1/")]
    [InlineData("https://[1:2:3:4:5:6:7:8:9]/")]
    [InlineData("https://[1:2:3:4:5:6:7]/")]
    [InlineData("https://[::1::2]/")]
    [InlineData("https://[::1.2.3]/")]
    [InlineData("https://[example.com]/")]
    [InlineData("ftp://example.com/")]
    [InlineData("chrome://settings/")]
    [InlineData("about:blank")]
    [InlineData("file:///C:/Users/noa/a.html")]
    [InlineData("data:text/html,hi")]
    [InlineData("javascript:alert(1)")]
    public void Normalize_RejectsInvalidOrNonWeb(string input) => Assert.False(UrlNormalizer.TryNormalize(input, out _));

    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("HTTP://example.com/", true)]
    [InlineData("  http://example.com", true)]
    [InlineData("https://", true)]
    [InlineData("https://exa mple.com/", true)]
    [InlineData("chrome://settings/", false)]
    [InlineData("edge://extensions/", false)]
    [InlineData("about:blank", false)]
    [InlineData("file:///C:/a.html", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("blob:https://example.com/123", false)]
    [InlineData("httpx://example.com/", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void IsWebUrl_ChecksTheScheme(string input, bool expected) => Assert.Equal(expected, UrlNormalizer.IsWebUrl(input));

    [Theory]
    [InlineData("Bücher.Example", "xn--bcher-kva.example", HostKind.Domain)]
    [InlineData(" EXAMPLE.com. ", "example.com", HostKind.Domain)]
    [InlineData("192.168.1.50", "192.168.1.50", HostKind.IPv4)]
    [InlineData("3232235826", "192.168.1.50", HostKind.IPv4)]
    [InlineData("[::1]", "::1", HostKind.IPv6)]
    [InlineData("::1", "::1", HostKind.IPv6)]
    [InlineData("[2001:DB8::0:1]", "2001:db8::1", HostKind.IPv6)]
    public void NormalizeHost_MatchesUrlForm(string input, string expected, HostKind kind)
    {
        Assert.True(UrlNormalizer.TryNormalizeHost(input, out var host, out var actualKind));
        Assert.Equal(expected, host);
        Assert.Equal(kind, actualKind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("exa mple.com")]
    [InlineData("example.com/path")]
    [InlineData("example.com:80")]
    [InlineData("http://example.com")]
    [InlineData("user@example.com")]
    [InlineData("1.2.3.4.5")]
    [InlineData("[::1")]
    public void NormalizeHost_RejectsNonHosts(string input) => Assert.False(UrlNormalizer.TryNormalizeHost(input, out _, out _));
}

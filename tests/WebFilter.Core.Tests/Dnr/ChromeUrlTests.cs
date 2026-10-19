namespace WebFilter.Core.Tests.Dnr;

/// <summary>
/// The simulator's URL canonicalization against what Edge 156 showed through declarativeNetRequest.testMatchOutcome
/// (each expected value below was observed in the browser while building the compiler).
/// </summary>
public class ChromeUrlTests
{
    [Theory]
    [InlineData("https://Blocked.Example./page", "https://blocked.example./page")]
    [InlineData("https://BLOCKED.EXAMPLE:8443/", "https://blocked.example:8443/")]
    [InlineData("http://blocked.example:80/", "http://blocked.example/")]
    [InlineData("https://blocked.example:443/", "https://blocked.example/")]
    [InlineData("https://x.example:08080/", "https://x.example:8080/")]
    [InlineData("https://user:secret@blocked.example/", "https://user:secret@blocked.example/")]
    [InlineData("https://u:@x.example/", "https://u@x.example/")]
    [InlineData("https://@x.example/", "https://x.example/")]
    [InlineData("https://allowed.example@blocked.example/", "https://allowed.example@blocked.example/")]
    [InlineData("https://blocked%2Eexample/", "https://blocked.example/")]
    [InlineData("https://blocked.example%2eother.example/", "https://blocked.example.other.example/")]
    [InlineData("https://blocked.example/../other.example/", "https://blocked.example/other.example/")]
    [InlineData("https://x.example/a/%2e%2e/b", "https://x.example/b")]
    [InlineData("https://x.example/a/b/./", "https://x.example/a/b/")]
    [InlineData("https://bücher.example/", "https://xn--bcher-kva.example/")]
    [InlineData("https://BÜCHER.example/", "https://xn--bcher-kva.example/")]
    [InlineData("http://3232235826/", "http://192.168.1.50/")]
    [InlineData("http://0xC0A80132/", "http://192.168.1.50/")]
    [InlineData("http://192.168.306/", "http://192.168.1.50/")]
    [InlineData("http://192.168.1.050/", "http://192.168.1.40/")]
    [InlineData("http://[::1]:8080/", "http://[::1]:8080/")]
    [InlineData("https://[0:0::1]/", "https://[::1]/")]
    [InlineData("https://x.example/a b", "https://x.example/a%20b")]
    [InlineData("https://x.example/€", "https://x.example/%E2%82%AC")]
    [InlineData("https://path.example/ש", "https://path.example/%D7%A9")]
    [InlineData("https://x.example/%72/learn", "https://x.example/%72/learn")]
    [InlineData("https://x.example/a%7eb", "https://x.example/a%7eb")]
    [InlineData("https://x.example/a%zzb", "https://x.example/a%zzb")]
    [InlineData("https://x.example/a{b|c^d", "https://x.example/a%7Bb%7Cc%5Ed")]
    [InlineData("https://x.example/a[b]'~", "https://x.example/a[b]'~")]
    [InlineData("https://x.example/?a{b|'c", "https://x.example/?a{b|%27c")]
    [InlineData("https://x.example/#a`b{", "https://x.example/#a%60b{")]
    [InlineData("https://x.example/?", "https://x.example/?")]
    [InlineData("https://x.example/#", "https://x.example/#")]
    [InlineData("https://frag.example/x#frag", "https://frag.example/x#frag")]
    public void Canonicalizes_LikeChrome(string input, string expected) => Assert.Equal(expected, ChromeUrl.Parse(input).Spec);

    [Theory]
    [InlineData("chrome://settings/")]
    [InlineData("about:blank")]
    [InlineData("file:///C:/a.html")]
    [InlineData("http://x.192.168.1.50/")]
    [InlineData("http://256.1.1.1/")]
    public void Rejects_WhatDnrNeverSeesAsAWebUrl(string input) => Assert.False(ChromeUrl.TryParse(input, out _, out _));

    [Fact]
    public void Href_DropsUserinfoFragmentAndTrailingDot() =>
        Assert.Equal("https://blocked.example/p?q", ChromeUrl.Parse("https://u:p@Blocked.Example./p?q#f").Href);
}

using WebFilter.Core.Model;
using WebFilter.Core.Urls;

namespace WebFilter.Core.Tests.Urls;

public class PatternMatcherTests
{
    private static NormalizedUrl Url(string url) =>
        UrlNormalizer.TryNormalize(url, out var normalized) ? normalized : throw new ArgumentException(url);

    private static Specificity? MatchOf(string pattern, PatternType type, string url) =>
        PatternMatcher.Compile(pattern, type).Matches(Url(url), out var specificity) ? specificity : null;

    [Theory]
    [InlineData("https://example.com/", 3, 2)]
    [InlineData("https://EXAMPLE.com:8443/x?y", 3, 2)]
    [InlineData("https://www.example.com/", 2, 2)]
    [InlineData("https://a.b.example.com/", 2, 2)]
    public void Domain_MatchesHostAndSubdomains(string url, int tier, int labels) =>
        Assert.Equal(new Specificity(tier, labels, 0), MatchOf("example.com", PatternType.Domain, url));

    [Theory]
    [InlineData("https://notexample.com/")]
    [InlineData("https://example.com.evil.net/")]
    [InlineData("https://example.co/")]
    [InlineData("https://com/")]
    [InlineData("http://93.184.216.34/")]
    public void Domain_DoesNotMatchLookAlikes(string url) => Assert.Null(MatchOf("example.com", PatternType.Domain, url));

    [Fact]
    public void Domain_IsNormalizedLikeUrls()
    {
        var pattern = PatternMatcher.Compile(" Bücher.Example. ", PatternType.Domain);
        Assert.Equal("xn--bcher-kva.example", pattern.Canonical);
        Assert.True(pattern.Matches(Url("https://www.BÜCHER.example/"), out _));
    }

    [Fact]
    public void Domain_IpLiteralMatchesThatAddressOnly()
    {
        Assert.Equal(new Specificity(3, 4, 0), MatchOf("192.168.1.50", PatternType.Domain, "http://3232235826/"));
        Assert.Null(MatchOf("192.168.1.50", PatternType.Domain, "http://192.168.1.5/"));
        Assert.Null(MatchOf("192.168.1.50", PatternType.Domain, "http://x.192.168.1.50.example/"));
        Assert.NotNull(MatchOf("[::1]", PatternType.Domain, "http://[0:0::1]:8080/"));
        Assert.NotNull(MatchOf("::1", PatternType.Domain, "http://[::1]/"));
        Assert.Null(MatchOf("::1", PatternType.Domain, "http://[::2]/"));
    }

    [Theory]
    [InlineData("https://a.example.com/", true)]
    [InlineData("https://a.b.example.com/", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://badexample.com/", false)]
    [InlineData("https://example.com.evil.net/", false)]
    public void Wildcard_MatchesSubdomainsOnly(string url, bool matches)
    {
        var match = MatchOf("*.example.com", PatternType.Wildcard, url);
        Assert.Equal(matches, match is not null);
        if (matches)
            Assert.Equal(new Specificity(Specificity.SubdomainTier, 2, 0), match);
    }

    [Theory]
    [InlineData("https://www.reddit.com/r/learn", true)]
    [InlineData("https://reddit.com/r/learn/", true)]
    [InlineData("https://reddit.com/r/learn/x/y", true)]
    [InlineData("https://reddit.com/R/Learn", true)]
    [InlineData("https://reddit.com/r/learn?sort=new", true)]
    [InlineData("https://reddit.com/r/learn#top", true)]
    [InlineData("https://reddit.com/r/learnmore", false)]
    [InlineData("https://reddit.com/r/lear", false)]
    [InlineData("https://reddit.com/x/r/learn", false)]
    [InlineData("https://notreddit.com/r/learn", false)]
    public void Prefix_MatchesAtSegmentBoundary(string url, bool matches)
    {
        var match = MatchOf("reddit.com/r/learn", PatternType.Prefix, url);
        Assert.Equal(matches, match is not null);
        if (matches)
            Assert.Equal(new Specificity(Specificity.PrefixTier, 2, "/r/learn".Length), match);
    }

    [Theory]
    [InlineData("https://reddit.com/r/", true)]
    [InlineData("https://reddit.com/r/funny", true)]
    [InlineData("https://reddit.com/r", false)]
    [InlineData("https://reddit.com/rx/", false)]
    public void Prefix_EndingInSlashIsPlainPrefix(string url, bool matches) =>
        Assert.Equal(matches, MatchOf("reddit.com/r/", PatternType.Prefix, url) is not null);

    [Theory]
    [InlineData("reddit.com/r/learn", "reddit.com/r/learn")]
    [InlineData("https://Reddit.COM/R/Learn?x=1#y", "reddit.com/r/learn")]
    [InlineData("reddit.com", "reddit.com/")]
    [InlineData("example.com/a/./b/../c", "example.com/a/c")]
    [InlineData("example.com/ש", "example.com/%d7%a9")]
    [InlineData("[::1]/admin", "[::1]/admin")]
    public void Prefix_CanonicalForm(string pattern, string canonical) =>
        Assert.Equal(canonical, PatternMatcher.Compile(pattern, PatternType.Prefix).Canonical);

    [Fact]
    public void Prefix_MatchesEncodedPathCaseInsensitively() =>
        Assert.NotNull(MatchOf("example.com/ש", PatternType.Prefix, "https://www.example.com/%D7%A9/page"));

    [Fact]
    public void Regex_MatchesFullHrefCaseInsensitively()
    {
        Assert.Equal(new Specificity(Specificity.RegexTier, 0, 0), MatchOf("^https://shop\\.example:8443/cart\\?id=", PatternType.Regex, "https://SHOP.example:8443/cart?id=1"));
        Assert.NotNull(MatchOf("CASINO", PatternType.Regex, "https://bestcasino.example/"));
        Assert.Null(MatchOf("#frag", PatternType.Regex, "https://example.com/#frag"));
    }

    [Theory]
    [InlineData("foo(?=bar)")]
    [InlineData("(?<!foo)bar")]
    [InlineData("(a)\\1")]
    [InlineData("(?>a+)b")]
    [InlineData("(")]
    [InlineData("[z-a]")]
    public void Regex_RejectsNonRe2AndInvalid(string pattern)
    {
        Assert.False(PatternMatcher.TryCompile(pattern, PatternType.Regex, out var compiled, out var error));
        Assert.Null(compiled);
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<ArgumentException>(() => PatternMatcher.Compile(pattern, PatternType.Regex));
    }

    [Theory]
    [InlineData("", PatternType.Domain)]
    [InlineData("   ", PatternType.Prefix)]
    [InlineData("*.example.com", PatternType.Domain)]
    [InlineData("exa mple.com", PatternType.Domain)]
    [InlineData("https://example.com/", PatternType.Domain)]
    [InlineData("example.com", PatternType.Wildcard)]
    [InlineData("*example.com", PatternType.Wildcard)]
    [InlineData("*.*.example.com", PatternType.Wildcard)]
    [InlineData("*.192.168.1.1", PatternType.Wildcard)]
    [InlineData("*.example.com/path", PatternType.Prefix)]
    [InlineData("exa mple.com/path", PatternType.Prefix)]
    public void TryCompile_RejectsInvalidPatterns(string pattern, PatternType type)
    {
        Assert.False(PatternMatcher.TryCompile(pattern, type, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Specificity_OrdersPrefixThenExactThenLabelsThenRegex()
    {
        const string url = "https://a.docs.google.com/x/y";
        (string Pattern, PatternType Type)[] mostToLeastSpecific =
        [
            ("docs.google.com/x/y", PatternType.Prefix),   // (4, 3, 4)
            ("docs.google.com/x", PatternType.Prefix),     // (4, 3, 2)
            ("google.com/x/y", PatternType.Prefix),        // (4, 2, 4): labels before path length
            ("a.docs.google.com", PatternType.Domain),     // (3, 4, 0) exact host
            ("docs.google.com", PatternType.Domain),       // (2, 3, 0) subdomain
            ("*.google.com", PatternType.Wildcard),        // (2, 2, 0)
            ("docs", PatternType.Regex),                   // (1, 0, 0)
        ];

        var specificities = mostToLeastSpecific.Select(p => MatchOf(p.Pattern, p.Type, url)!.Value).ToList();
        for (var i = 1; i < specificities.Count; i++)
            Assert.True(specificities[i - 1] > specificities[i], $"{mostToLeastSpecific[i - 1]} should beat {mostToLeastSpecific[i]}");
    }

    [Fact]
    public void Specificity_SubdomainDomainAndWildcardWithSameBaseTie()
    {
        var domain = MatchOf("docs.google.com", PatternType.Domain, "https://a.docs.google.com/");
        var wildcard = MatchOf("*.docs.google.com", PatternType.Wildcard, "https://a.docs.google.com/");
        Assert.Equal(domain, wildcard);
        Assert.True(domain >= wildcard && domain <= wildcard);
    }

    [Fact]
    public void Specificity_Operators()
    {
        var low = new Specificity(2, 5, 9);
        var high = new Specificity(3, 1, 0);
        Assert.True(high > low);
        Assert.True(low < high);
        Assert.True(new Specificity(2, 3, 1) > new Specificity(2, 2, 9));
        Assert.True(new Specificity(4, 2, 10) > new Specificity(4, 2, 3));
        Assert.Equal(0, new Specificity(1, 0, 0).CompareTo(new Specificity(1, 0, 0)));
    }
}

using System.Diagnostics;
using WebFilter.Core.Decisions;
using WebFilter.Core.Model;
using WebFilter.Core.Urls;
using Xunit.Abstractions;

namespace WebFilter.Core.Tests.Urls;

public class RuleEvaluatorTests(ITestOutputHelper output)
{
    private static NormalizedUrl Url(string url) =>
        UrlNormalizer.TryNormalize(url, out var normalized) ? normalized : throw new ArgumentException(url);

    private static EffectiveUrlPolicy Policy(FilterMode mode, Rule[]? member = null, Rule[]? global = null, CategoryDomain[]? categories = null) =>
        new("kid", mode, YouTubeMode.Restricted, member ?? [], global ?? [], categories ?? []);

    private static Rule Block(string id, string pattern, PatternType type = PatternType.Domain) => new(id, null, pattern, type, RuleAction.Block);
    private static Rule Allow(string id, string pattern, PatternType type = PatternType.Domain, bool always = false) =>
        new(id, null, pattern, type, RuleAction.Allow, always);

    [Fact]
    public void MemberTie_BlockWins()
    {
        var policy = Policy(FilterMode.Blocklist, member: [Allow("a", "*.example.com", PatternType.Wildcard), Block("b", "example.com")]);
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedRule, "b"), RuleEvaluator.Evaluate(policy, Url("https://www.example.com/")));
    }

    [Fact]
    public void GlobalTie_BetweenRuleAndCategory_BlockWins()
    {
        var categoryBlocks = Policy(FilterMode.Blocklist, global: [Allow("g", "games.example")],
            categories: [new CategoryDomain("games.example", RuleAction.Block, "gaming")]);
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedCategory), RuleEvaluator.Evaluate(categoryBlocks, Url("https://games.example/")));

        var ruleBlocks = Policy(FilterMode.Allowlist, global: [Block("g", "wiki.example")],
            categories: [new CategoryDomain("wiki.example", RuleAction.Allow, "educational")]);
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedRule, "g"), RuleEvaluator.Evaluate(ruleBlocks, Url("https://wiki.example/")));
    }

    [Fact]
    public void GlobalTie_SameAction_RuleReasonBeatsCategory()
    {
        var policy = Policy(FilterMode.Allowlist, global: [Allow("g", "wiki.example")],
            categories: [new CategoryDomain("wiki.example", RuleAction.Allow, "educational")]);
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedRule, "g"), RuleEvaluator.Evaluate(policy, Url("https://wiki.example/")));
    }

    [Fact]
    public void MoreSpecificCategoryBeatsLessSpecificGlobalRule()
    {
        var policy = Policy(FilterMode.Blocklist, global: [Block("g", "*.example.com", PatternType.Wildcard)],
            categories: [new CategoryDomain("learn.example.com", RuleAction.Allow, "educational")]);
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedCategory), RuleEvaluator.Evaluate(policy, Url("https://learn.example.com/")));
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedRule, "g"), RuleEvaluator.Evaluate(policy, Url("https://play.example.com/")));
    }

    [Fact]
    public void MemberRuleBeatsMoreSpecificGlobalRuleAndCategory()
    {
        var policy = Policy(FilterMode.Blocklist,
            member: [Allow("m", "example.com")],
            global: [Block("g", "example.com/a/b", PatternType.Prefix)],
            categories: [new CategoryDomain("www.example.com", RuleAction.Block, "social")]);
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedRule, "m"), RuleEvaluator.Evaluate(policy, Url("https://www.example.com/a/b")));
    }

    [Fact]
    public void AlwaysAllowedRule_ReportsAllowedAlways_ForMemberAndGlobal()
    {
        var policy = Policy(FilterMode.Allowlist,
            member: [new Rule("m", "kid", "school.example", PatternType.Domain, RuleAction.Allow, AlwaysAllowed: true)],
            global: [Allow("g", "mashov.info", always: true)]);
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedAlways, "m"), RuleEvaluator.Evaluate(policy, Url("https://www.school.example/")));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedAlways, "g"), RuleEvaluator.Evaluate(policy, Url("https://mashov.info/login")));
    }

    [Fact]
    public void Defaults_FollowTheMode()
    {
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedDefault), RuleEvaluator.Evaluate(Policy(FilterMode.Blocklist), Url("https://a.example/")));
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedNotInAllowlist), RuleEvaluator.Evaluate(Policy(FilterMode.Allowlist), Url("https://a.example/")));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedUnrestricted), RuleEvaluator.Evaluate(Policy(FilterMode.Unrestricted, member: [Block("b", "a.example")]), Url("https://a.example/")));
    }

    [Fact]
    public void IpLiteral_BlockedUnlessAnAllowRuleMatches()
    {
        var policy = Policy(FilterMode.Blocklist,
            member: [Block("m-block", "^http://10\\.", PatternType.Regex)],
            global: [Allow("printer", "192.168.1.50"), Allow("nas", "10.0.0.5"), Allow("v6", "[fe80::1]"), Block("b", "192.168.1.60")]);

        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedRule, "printer"), RuleEvaluator.Evaluate(policy, Url("http://0xC0A80132/")));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedRule, "v6"), RuleEvaluator.Evaluate(policy, Url("http://[FE80:0::1]:8080/")));
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedIpLiteral), RuleEvaluator.Evaluate(policy, Url("http://192.168.1.60/")));
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedIpLiteral), RuleEvaluator.Evaluate(policy, Url("http://[::1]/")));
        // Allowed past the IP step, then the member rule still wins.
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedRule, "m-block"), RuleEvaluator.Evaluate(policy, Url("http://10.0.0.5/")));
    }

    [Fact]
    public void InvalidPatterns_NeverMatch()
    {
        var policy = Policy(FilterMode.Blocklist, global: [Block("bad-regex", "(?=x)x", PatternType.Regex), Block("bad-wild", "example.com", PatternType.Wildcard)]);
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedDefault), RuleEvaluator.Evaluate(policy, Url("https://www.example.com/x")));
    }

    [Fact]
    public void ManyRegexRules_PrefilterKeepsEachPatternsOwnSemantics()
    {
        // 70 regex rules span several prefilter chunks; inline options must stay scoped to their own pattern.
        var global = Enumerable.Range(0, 68).Select(i => Block($"x{i}", $"^https://filler{i}\\.example/", PatternType.Regex)).ToList();
        global.Insert(10, Block("case-sensitive", "(?-i)/CaseOnly$", PatternType.Regex));
        global.Add(Allow("anchored", "^https://ok\\.example/(a|b)+$", PatternType.Regex));
        var policy = Policy(FilterMode.Blocklist, global: [.. global]);

        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedRule, "case-sensitive"), RuleEvaluator.Evaluate(policy, Url("https://x.example/CaseOnly")));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedDefault), RuleEvaluator.Evaluate(policy, Url("https://x.example/caseonly")));
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedRule, "x67"), RuleEvaluator.Evaluate(policy, Url("https://FILLER67.example/z")));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedRule, "anchored"), RuleEvaluator.Evaluate(policy, Url("https://ok.example/abba")));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedDefault), RuleEvaluator.Evaluate(policy, Url("https://ok.example/abc")));
    }

    [Fact]
    public void MatchesAlwaysAllowed_FindsMemberAndGlobalAllowRulesOnly()
    {
        var policy = Policy(FilterMode.Allowlist,
            member: [new Rule("m", "kid", "school.example", PatternType.Domain, RuleAction.Allow, AlwaysAllowed: true)],
            global: [Allow("g", "mashov.info", always: true), Allow("plain", "pbskids.org"), new Rule("odd", null, "odd.example", PatternType.Domain, RuleAction.Block, AlwaysAllowed: true)]);

        Assert.True(RuleEvaluator.MatchesAlwaysAllowed(policy, Url("https://web.mashov.info/"), out var id));
        Assert.Equal("g", id);
        Assert.True(RuleEvaluator.MatchesAlwaysAllowed(policy, Url("https://school.example/"), out id));
        Assert.Equal("m", id);
        Assert.False(RuleEvaluator.MatchesAlwaysAllowed(policy, Url("https://pbskids.org/"), out id));
        Assert.Null(id);
        Assert.False(RuleEvaluator.MatchesAlwaysAllowed(policy, Url("https://odd.example/"), out _));
    }

    [Fact]
    public void IsInCategory_MatchesDomainsAndSubdomains()
    {
        var educational = new List<string> { "Wikipedia.org", "bücher.example", "scratch.mit.edu" };
        var categories = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { ["educational"] = educational };

        Assert.True(RuleEvaluator.IsInCategory(categories, "educational", Url("https://he.wikipedia.org/wiki/x")));
        Assert.True(RuleEvaluator.IsInCategory(categories, "Educational", Url("https://xn--bcher-kva.example/")));
        Assert.True(RuleEvaluator.IsInCategory(categories, "educational", Url("https://scratch.mit.edu/")));
        Assert.False(RuleEvaluator.IsInCategory(categories, "educational", Url("https://mit.edu/")));
        Assert.False(RuleEvaluator.IsInCategory(categories, "educational", Url("https://wikipedia.org.evil.net/")));
        Assert.False(RuleEvaluator.IsInCategory(categories, "gaming", Url("https://he.wikipedia.org/")));

        educational.Add("khanacademy.org");
        Assert.True(RuleEvaluator.IsInCategory(categories, "educational", Url("https://www.khanacademy.org/")));
    }

    [Fact]
    public void TenThousandRules_TenThousandEvaluations_StayFast()
    {
        var member = new List<Rule>();
        var global = new List<Rule>();
        for (var i = 0; i < 6000; i++)
            (i % 5 == 0 ? member : global).Add(new Rule($"d{i}", i % 5 == 0 ? "kid" : null, $"site{i}.example", PatternType.Domain, i % 2 == 0 ? RuleAction.Block : RuleAction.Allow));
        for (var i = 0; i < 2000; i++)
            global.Add(Block($"w{i}", $"*.wild{i}.example", PatternType.Wildcard));
        for (var i = 0; i < 1900; i++)
            global.Add(Allow($"p{i}", $"prefix{i % 100}.example/path{i}", PatternType.Prefix));
        for (var i = 0; i < 100; i++)
            global.Add(Block($"x{i}", $"^https?://([a-z0-9-]+\\.)*rx{i}[a-z]*\\.example/", PatternType.Regex));
        var categories = Enumerable.Range(0, 2000).Select(i => new CategoryDomain($"cat{i}.example", RuleAction.Block, "social")).ToList();
        var policy = new EffectiveUrlPolicy("kid", FilterMode.Blocklist, YouTubeMode.Restricted, member, global, categories);
        Assert.Equal(10_000, member.Count + global.Count);

        var urls = Enumerable.Range(0, 10_000).Select(i => Url((i % 6) switch
        {
            0 => $"https://www.site{i % 6000}.example/a/b?c=d",
            1 => $"https://a.b.wild{i % 2000}.example/",
            2 => $"https://prefix{i % 100}.example/path{i % 1900}/x",
            3 => $"https://rx{i % 100}foo.example/play",
            4 => $"https://www.cat{i % 2000}.example/",
            _ => $"https://unknown{i}.deep.sub.domain.test/some/long/path?q={i}",
        })).ToList();

        var build = Stopwatch.StartNew();
        RuleEvaluator.Evaluate(policy, Url("https://warm.example/"));
        var coldBuild = build.Elapsed;
        // A new policy over the same Rule instances reuses their compiled patterns and only rebuilds the index.
        var rebuilt = policy with { MemberRules = [.. member], GlobalRules = [.. global] };
        build.Restart();
        RuleEvaluator.Evaluate(rebuilt, Url("https://warm.example/"));
        output.WriteLine($"index build: cold {coldBuild.TotalMilliseconds:F1} ms, rebuild with cached patterns {build.Elapsed.TotalMilliseconds:F1} ms");

        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedRule, "d10"), RuleEvaluator.Evaluate(policy, Url("https://site10.example/")));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedRule, "d11"), RuleEvaluator.Evaluate(policy, Url("https://site11.example/")));
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedRule, "x7"), RuleEvaluator.Evaluate(policy, Url("https://rx7abc.example/")));
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedCategory), RuleEvaluator.Evaluate(policy, Url("https://cat5.example/")));

        var timings = new double[urls.Count];
        var total = Stopwatch.StartNew();
        for (var i = 0; i < urls.Count; i++)
        {
            var start = Stopwatch.GetTimestamp();
            RuleEvaluator.Evaluate(policy, urls[i]);
            timings[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        total.Stop();

        Array.Sort(timings);
        output.WriteLine($"10,000 evaluations: {total.ElapsedMilliseconds} ms total, p50 {timings[5_000]:F4} ms, p99 {timings[9_900]:F4} ms, max {timings[^1]:F4} ms");
        // The median, not the wall-clock total: the full suite runs the test assemblies in parallel, and the total then
        // measures the machine's load (it reached seconds) rather than the evaluator. A median of a few microseconds is
        // normal; scanning the 10,000 rules on every call would put it far above 0.1 ms. Real timings: WebFilter.Benchmarks.
        Assert.True(timings[5_000] < 0.1, $"median evaluation took {timings[5_000]:F4} ms");
    }
}

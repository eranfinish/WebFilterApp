using WebFilter.Core.Dnr;
using WebFilter.Core.Model;
using WebFilter.Core.Urls;
using static WebFilter.Core.Tests.Dnr.DnrTestPolicies;

namespace WebFilter.Core.Tests.Dnr;

/// <summary>
/// Random policies over a small universe of overlapping hosts and paths: the compiled rules (through <see cref="DnrSimulator"/>)
/// must agree with <see cref="ReferenceEvaluator"/> on every URL, for the standing rules and for the time-up session rules.
/// This exercises the priority layout far beyond the scenario catalog (member vs global, ties, exact vs subdomain, wildcards,
/// prefixes, regexes, categories, IP literals, YouTube).
/// </summary>
public class DnrDifferentialTests
{
    private static readonly string[] Hosts =
        ["example.com", "a.example.com", "b.a.example.com", "example.org", "x.example.org", "com", "bücher.example", "192.168.1.50", "10.0.0.1", "[::1]", "youtube.com"];

    // Category lists hold registrable domains. An IP literal in an allow category is ignored by the compiler (categories never
    // lift the IP-literal block); that can over-block in contrived combinations, so the oracle comparison leaves IPs out.
    private static readonly string[] CategoryHosts = ["example.com", "a.example.com", "b.a.example.com", "example.org", "x.example.org", "com", "bücher.example", "youtube.com"];

    private static readonly string[] PrefixPaths = ["", "/", "/r", "/r/", "/r/learn", "/R/Learn/deep", "/ש"];

    private static readonly string[] Regexes =
    [
        @"^https?://([a-z0-9-]+\.)*example\.com/",
        "learn",
        @"^https?://[^/]*\.org/r/$",
        @"^http://10\.",
        "/R/",
    ];

    private static readonly string[] UrlHosts =
    [
        "example.com", "a.example.com", "b.a.example.com", "c.b.a.example.com", "Example.COM.", "example.org", "x.example.org", "y.x.example.org",
        "bücher.example", "www.xn--bcher-kva.example", "192.168.1.50", "3232235826", "10.0.0.1", "[::1]", "[0:0::1]", "other.net",
        "www.youtube.com", "youtu.be", "www.youtu.be", "notyoutube.com",
    ];

    private static readonly string[] UrlPaths = ["/", "/r", "/r/", "/r/learn", "/r/learnmore", "/R/LEARN/deep?x=1", "/r/learn#frag", "/ש", "/a%20b"];

    [Fact]
    public void RandomPolicies_MatchTheReferenceEvaluator()
    {
        var random = new Random(20261019);
        var urls = (from host in UrlHosts
                    from path in UrlPaths
                    from userinfo in new[] { "", "u:p@" }
                    select ChromeUrl.Parse($"http{(path.Length % 2 == 0 ? "s" : "")}://{userinfo}{host}{path}")).ToList();
        var failures = new List<string>();

        for (var iteration = 0; iteration < 400 && failures.Count < 10; iteration++)
        {
            var policy = RandomPolicy(random);
            var rules = DnrCompiler.Compile(policy, Options);
            var hasRegex = policy.MemberRules.Concat(policy.GlobalRules).Any(r => r.Type == PatternType.Regex);
            foreach (var url in urls)
            {
                // Known gap: Chrome keeps a trailing host dot in the URL a regexFilter sees, the evaluator does not.
                if (hasRegex && url.Host.EndsWith('.'))
                    continue;
                var expected = ReferenceEvaluator.Evaluate(policy, url);
                var actual = DnrSimulator.Evaluate(rules.DynamicRules, url);
                if (expected != actual)
                    failures.Add($"#{iteration} {url}: reference {expected}, DNR {actual}\n  policy: {Describe(policy)}");

                var expectedTimeUp = ReferenceEvaluator.EvaluateTimeUp(policy, url);
                var actualTimeUp = DnrSimulator.Evaluate(rules.TimeUpSessionRules.Concat(rules.DynamicRules), url);
                if (expectedTimeUp != actualTimeUp)
                    failures.Add($"#{iteration} time-up {url}: reference {expectedTimeUp}, DNR {actualTimeUp}\n  policy: {Describe(policy)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The browser fallback and the decider use <see cref="RuleEvaluator"/>; the extension uses the compiled rules. Both must
    /// reach the same decision. YouTube hosts are left out (the engine decides YouTube before the evaluator runs).
    /// </summary>
    [Fact]
    public void RandomPolicies_CoreEvaluatorAgreesWithTheCompiledRules()
    {
        var random = new Random(20261020);
        var urls = (from host in UrlHosts
                    where !host.Contains("youtu", StringComparison.Ordinal)
                    from path in UrlPaths
                    from userinfo in new[] { "", "u:p@" }
                    select $"http{(path.Length % 2 == 0 ? "s" : "")}://{userinfo}{host}{path}").ToList();
        var failures = new List<string>();

        for (var iteration = 0; iteration < 400 && failures.Count < 10; iteration++)
        {
            var policy = RandomPolicy(random);
            var rules = DnrCompiler.Compile(policy, Options);
            var hasRegex = policy.MemberRules.Concat(policy.GlobalRules).Any(r => r.Type == PatternType.Regex);
            foreach (var raw in urls)
            {
                var chrome = ChromeUrl.Parse(raw);
                if (hasRegex && chrome.Host.EndsWith('.'))
                    continue;
                Assert.True(UrlNormalizer.TryNormalize(raw, out var normalized), raw);
                var core = RuleEvaluator.Evaluate(policy, normalized).Decision == Decisions.Decision.Allow ? DnrOutcome.Allow : DnrOutcome.Block;
                var dnr = DnrSimulator.Evaluate(rules.DynamicRules, chrome);
                if (core != dnr)
                    failures.Add($"#{iteration} {raw}: core {core}, DNR {dnr}\n  policy: {Describe(policy)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static EffectiveUrlPolicy RandomPolicy(Random random)
    {
        Rule RandomRule(bool member)
        {
            var action = random.Next(2) == 0 ? RuleAction.Allow : RuleAction.Block;
            var always = action == RuleAction.Allow && random.Next(4) == 0;
            var host = Hosts[random.Next(Hosts.Length)];
            var (pattern, type) = random.Next(5) switch
            {
                0 or 1 => (host, PatternType.Domain),
                2 when !host.StartsWith('[') && !char.IsAsciiDigit(host[0]) => ("*." + host, PatternType.Wildcard),
                2 or 3 => (host + PrefixPaths[random.Next(PrefixPaths.Length)], PatternType.Prefix),
                _ => (Regexes[random.Next(Regexes.Length)], PatternType.Regex),
            };
            return member ? Member(pattern, type, action, always) : Global(pattern, type, action, always);
        }

        var memberRules = Enumerable.Range(0, random.Next(7)).Select(_ => RandomRule(true)).ToList();
        var globalRules = Enumerable.Range(0, random.Next(7)).Select(_ => RandomRule(false)).ToList();
        var categories = Enumerable.Range(0, random.Next(5))
            .Select(_ => new CategoryDomain(CategoryHosts[random.Next(CategoryHosts.Length)], random.Next(2) == 0 ? RuleAction.Allow : RuleAction.Block, "c"))
            .ToList();
        var mode = random.Next(2) == 0 ? FilterMode.Allowlist : FilterMode.Blocklist;
        var youTube = (YouTubeMode)random.Next(4);
        return Policy(mode, youTube, memberRules, globalRules, categories);
    }

    private static string Describe(EffectiveUrlPolicy p) =>
        $"{p.Mode}, YouTube {p.YouTube}; member [{string.Join(", ", p.MemberRules.Select(R))}]; global [{string.Join(", ", p.GlobalRules.Select(R))}]; " +
        $"categories [{string.Join(", ", p.CategoryDomains.Select(c => $"{c.Action} {c.Domain}"))}]";

    private static string R(Rule r) => $"{r.Action} {r.Type} {r.Pattern}{(r.AlwaysAllowed ? " (always)" : "")}";
}

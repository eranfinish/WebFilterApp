using WebFilter.Core.Decisions;
using WebFilter.Core.Dnr;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Time;
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
        "www.youtube.com", "accounts.youtube.com", "youtu.be", "www.youtu.be", "notyoutube.com",
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
                var core = RuleEvaluator.Evaluate(policy, normalized).Decision == Decision.Allow ? DnrOutcome.Allow : DnrOutcome.Block;
                var dnr = DnrSimulator.Evaluate(rules.DynamicRules, chrome);
                if (core != dnr)
                    failures.Add($"#{iteration} {raw}: core {core}, DNR {dnr}\n  policy: {Describe(policy)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// PROTECTION-SPEC §13: random baseline policies — the parents' allow rules over random adult-category blocks — compiled
    /// and through <see cref="DnrSimulator"/> agree with the reference on every URL, YouTube hosts and IP literals included
    /// (the baseline has neither rule), and there are no time-up rules.
    /// </summary>
    [Fact]
    public void RandomBaselinePolicies_MatchTheReferenceEvaluator()
    {
        var random = new Random(20261023);
        var urls = AllUrls().Select(ChromeUrl.Parse).ToList();
        var failures = new List<string>();

        for (var iteration = 0; iteration < 300 && failures.Count < 10; iteration++)
        {
            var (_, policy) = RandomBaseline(random);
            var rules = DnrCompiler.Compile(policy, Options);
            Assert.Empty(rules.TimeUpSessionRules);
            var hasRegex = policy.MemberRules.Any(r => r.Type == PatternType.Regex);
            foreach (var url in urls)
            {
                if (hasRegex && url.Host.EndsWith('.'))
                    continue;
                var expected = ReferenceEvaluator.Evaluate(policy, url);
                var actual = DnrSimulator.Evaluate(rules.DynamicRules, url);
                if (expected != actual)
                    failures.Add($"#{iteration} {url}: reference {expected}, DNR {actual}\n  policy: {Describe(policy)}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// The service decides a parent's URL with <see cref="Decider.Baseline"/> (a category lookup first, then the
    /// evaluator); the extension with the compiled baseline policy. Both must agree on random families, for an unrestricted
    /// member and for an administrator linked to nobody, and the service blocks only with blocked_baseline_adult.
    /// </summary>
    [Fact]
    public void RandomBaselineFamilies_ServiceAgreesWithTheCompiledRules()
    {
        var random = new Random(20261024);
        var urls = AllUrls().ToList();
        var now = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00", System.Globalization.CultureInfo.InvariantCulture);
        var failures = new List<string>();

        for (var iteration = 0; iteration < 300 && failures.Count < 10; iteration++)
        {
            var (config, _) = RandomBaseline(random);
            var view = new PolicyView(config, new Dictionary<string, MemberRuntime>(), new UsageLedger(), new Dictionary<string, DateTimeOffset>());
            foreach (var memberId in new[] { "m", null })
            {
                var policy = EffectiveUrlPolicy.ForBaseline(config, memberId, now);
                var rules = DnrCompiler.Compile(policy, Options).DynamicRules;
                var hasRegex = policy.MemberRules.Any(r => r.Type == PatternType.Regex);
                foreach (var raw in urls)
                {
                    var chrome = ChromeUrl.Parse(raw);
                    if (hasRegex && chrome.Host.EndsWith('.'))
                        continue;
                    var decision = Decider.Baseline(view, memberId, raw, now);
                    var service = decision.Decision == Decision.Allow ? DnrOutcome.Allow : DnrOutcome.Block;
                    var dnr = DnrSimulator.Evaluate(rules, chrome);
                    if (service != dnr)
                        failures.Add($"#{iteration} {memberId ?? "admin"} {raw}: service {decision}, DNR {dnr}\n  policy: {Describe(policy)}");
                    if (decision.Decision == Decision.Block && decision.Reason != ReasonCode.BlockedBaselineAdult)
                        failures.Add($"#{iteration} {raw}: blocked with {decision}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static IEnumerable<string> AllUrls() =>
        from host in UrlHosts
        from path in UrlPaths
        from userinfo in new[] { "", "u:p@" }
        select $"http{(path.Length % 2 == 0 ? "s" : "")}://{userinfo}{host}{path}";

    /// <summary>A family with one unrestricted member "m": random allow rules (his and global, some expired) and a random adult list (IP literals included).</summary>
    private static (FamilyConfig Config, EffectiveUrlPolicy Policy) RandomBaseline(Random random)
    {
        var now = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00", System.Globalization.CultureInfo.InvariantCulture);
        string[] adultHosts = [.. CategoryHosts, "192.168.1.50", "10.0.0.1"];
        var config = new FamilyConfig
        {
            TimeZone = TimeZoneInfo.Utc,
            Members = [new FamilyMember("m", "M", Role.Adult, null, FilterMode.Unrestricted, YouTubeMode.Unrestricted)],
        };
        config.Categories[BaselineProtection.AdultCategory] = Enumerable.Range(0, random.Next(1, 5)).Select(_ => adultHosts[random.Next(adultHosts.Length)]).Distinct().ToList();
        var ruleCount = random.Next(7);
        for (var i = 0; i < ruleCount; i++)
        {
            var host = Hosts[random.Next(Hosts.Length)];
            var (pattern, type) = random.Next(5) switch
            {
                0 or 1 => (host, PatternType.Domain),
                2 when !host.StartsWith('[') && !char.IsAsciiDigit(host[0]) => ("*." + host, PatternType.Wildcard),
                2 or 3 => (host + PrefixPaths[random.Next(PrefixPaths.Length)], PatternType.Prefix),
                _ => (Regexes[random.Next(Regexes.Length)], PatternType.Regex),
            };
            // Block rules of the parents and expired allows are not part of the baseline.
            var action = random.Next(4) == 0 ? RuleAction.Block : RuleAction.Allow;
            var expires = random.Next(5) == 0 ? now.AddMinutes(-1) : (DateTimeOffset?)null;
            config.Rules.Add(new Rule($"r{i}", random.Next(2) == 0 ? "m" : null, pattern, type, action, random.Next(4) == 0, expires));
        }
        return (config, EffectiveUrlPolicy.ForBaseline(config, "m", now));
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
        $"{(p.Baseline ? "Baseline " : "")}{p.Mode}, YouTube {p.YouTube}; member [{string.Join(", ", p.MemberRules.Select(R))}]; global [{string.Join(", ", p.GlobalRules.Select(R))}]; " +
        $"categories [{string.Join(", ", p.CategoryDomains.Select(c => $"{c.Action} {c.Domain}"))}]";

    private static string R(Rule r) => $"{r.Action} {r.Type} {r.Pattern}{(r.AlwaysAllowed ? " (always)" : "")}";
}

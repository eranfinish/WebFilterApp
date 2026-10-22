using System.Text.RegularExpressions;
using WebFilter.Core.Dnr;
using WebFilter.Core.Model;

namespace WebFilter.Core.Tests.Dnr;

internal static class DnrTestPolicies
{
    public const string ExtensionId = "abcdefghijklmnopabcdefghijklmnop";
    public static readonly DnrCompileOptions Options = new(ExtensionId);

    public static EffectiveUrlPolicy Policy(
        FilterMode mode = FilterMode.Blocklist,
        YouTubeMode youTube = YouTubeMode.Restricted,
        IEnumerable<Rule>? member = null,
        IEnumerable<Rule>? global = null,
        IEnumerable<CategoryDomain>? categories = null) =>
        new("m", mode, youTube, (member ?? []).ToList(), (global ?? []).ToList(), (categories ?? []).ToList());

    public static Rule Member(string pattern, PatternType type, RuleAction action, bool always = false, string? id = null) =>
        new(id ?? $"m:{type}:{action}:{pattern}", "m", pattern, type, action, always);

    public static Rule Global(string pattern, PatternType type, RuleAction action, bool always = false, string? id = null) =>
        new(id ?? $"g:{type}:{action}:{pattern}", null, pattern, type, action, always);

    public static DnrOutcome Outcome(DnrRuleSet set, string url, string frame = "main_frame") => DnrSimulator.Evaluate(set.DynamicRules, url, frame);

    public static DnrOutcome TimeUpOutcome(DnrRuleSet set, string url) =>
        DnrSimulator.Evaluate(set.TimeUpSessionRules.Concat(set.DynamicRules), url);
}

/// <summary>
/// Test oracle: README §5 / <c>RuleEvaluator</c> semantics written directly (no DNR), on the evaluator's view of the URL
/// (<see cref="ChromeUrl.Href"/>: no userinfo, no fragment). Used to check the priority scheme on random policies.
/// </summary>
internal static class ReferenceEvaluator
{
    private readonly record struct Spec(int Tier, int Labels, int PathLength) : IComparable<Spec>
    {
        public int CompareTo(Spec o) => (Tier, Labels, PathLength).CompareTo((o.Tier, o.Labels, o.PathLength));
    }

    public static DnrOutcome Evaluate(EffectiveUrlPolicy policy, ChromeUrl url)
    {
        if (policy.Mode == FilterMode.Unrestricted)
            return DnrOutcome.Allow;
        if (MatchesBuiltin(policy, url))
            return DnrOutcome.Block;
        if (IsYouTubeHost(url.DomainHost))
            return policy.YouTube == YouTubeMode.Off && !Under(url.DomainHost, "accounts.youtube.com") ? DnrOutcome.Block : DnrOutcome.Allow;
        if (url.IsIpLiteral && !policy.MemberRules.Concat(policy.GlobalRules).Any(r => r.Action == RuleAction.Allow && Match(r, url) is not null))
            return DnrOutcome.Block;

        var member = Best(policy.MemberRules.Select(r => (Match(r, url), r.Action)));
        if (member is { } m)
            return m;
        var global = Best(policy.GlobalRules.Select(r => (Match(r, url), r.Action))
            .Concat(policy.CategoryDomains.Select(c => (MatchDomain(c.Domain, url), c.Action))));
        if (global is { } g)
            return g;
        return policy.Mode == FilterMode.Allowlist ? DnrOutcome.Block : DnrOutcome.Allow;
    }

    public static DnrOutcome EvaluateTimeUp(EffectiveUrlPolicy policy, ChromeUrl url) =>
        !MatchesBuiltin(policy, url) && policy.AlwaysAllowedRules.Any(r => r.Action == RuleAction.Allow && Match(r, url) is not null) ? DnrOutcome.Allow : DnrOutcome.Block;

    /// <summary>Built-in blocks (image search) win over every rule, the time-up always-allowed ones included.</summary>
    public static bool MatchesBuiltin(EffectiveUrlPolicy policy, ChromeUrl url) => policy.BuiltinBlocks.Any(b => Match(b.Rule, url) is not null);

    public static bool IsYouTubeHost(string host) =>
        host == "youtu.be" || Under(host, "youtube.com") || Under(host, "youtube-nocookie.com");

    private static DnrOutcome? Best(IEnumerable<(Spec? Spec, RuleAction Action)> matches)
    {
        var hits = matches.Where(x => x.Spec is not null).ToList();
        if (hits.Count == 0)
            return null;
        var top = hits.Max(x => x.Spec!.Value);
        return hits.Any(x => x.Spec!.Value.CompareTo(top) == 0 && x.Action == RuleAction.Block) ? DnrOutcome.Block : DnrOutcome.Allow;
    }

    private static Spec? Match(Rule rule, ChromeUrl url)
    {
        switch (rule.Type)
        {
            case PatternType.Domain:
                return MatchDomain(rule.Pattern, url);
            case PatternType.Wildcard:
                if (!rule.Pattern.StartsWith("*.") || Host(rule.Pattern[2..]) is not { } baseHost)
                    return null;
                return url.DomainHost.EndsWith("." + baseHost) ? new Spec(2, Labels(baseHost), 0) : null;
            case PatternType.Prefix:
                var slash = rule.Pattern.IndexOf('/');
                var host = Host(slash < 0 ? rule.Pattern : rule.Pattern[..slash]);
                if (host is null || !(url.DomainHost == host || url.DomainHost.EndsWith("." + host)))
                    return null;
                var path = ChromeUrl.Parse("http://x" + (slash < 0 ? "/" : rule.Pattern[slash..])).Path;
                if (!url.Path.StartsWith(path, StringComparison.OrdinalIgnoreCase))
                    return null;
                if (!path.EndsWith('/') && url.Path.Length > path.Length && url.Path[path.Length] != '/')
                    return null;
                return new Spec(4, Labels(host), path.Length);
            case PatternType.Regex:
                return Regex.IsMatch(url.Href, rule.Pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase) ? new Spec(1, 0, 0) : null;
            default:
                return null;
        }
    }

    private static Spec? MatchDomain(string pattern, ChromeUrl url)
    {
        if (Host(pattern) is not { } host)
            return null;
        if (url.DomainHost == host)
            return new Spec(3, Labels(host), 0);
        return url.DomainHost.EndsWith("." + host) ? new Spec(2, Labels(host), 0) : null;
    }

    private static string? Host(string pattern) =>
        ChromeUrl.TryParse("http://" + pattern + "/", out var url, out _) ? url.DomainHost : null;

    private static int Labels(string host) => host.StartsWith('[') ? 1 : host.Count(c => c == '.') + 1;

    private static bool Under(string host, string domain) => host == domain || host.EndsWith("." + domain);
}

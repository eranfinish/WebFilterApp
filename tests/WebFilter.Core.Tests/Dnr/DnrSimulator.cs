using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WebFilter.Core.Dnr;

namespace WebFilter.Core.Tests.Dnr;

internal enum DnrOutcome { Allow, Block }

/// <summary>
/// Test-only model of Chrome's declarativeNetRequest matching (developer.chrome.com/docs/extensions/reference/api/declarativeNetRequest):
/// a rule matches when its resourceTypes contain the request type, the host equals or is a subdomain of an entry in
/// requestDomains and of none in excludedRequestDomains, and regexFilter matches the canonical URL (case-insensitive unless
/// isUrlFilterCaseSensitive). The highest priority wins across all rule sets; at equal priority
/// allow &gt; allowAllRequests &gt; block &gt; upgradeScheme &gt; redirect.
/// </summary>
internal static class DnrSimulator
{
    private static readonly ConcurrentDictionary<(string, bool), Regex> Regexes = new();

    public static DnrRule? Match(IEnumerable<DnrRule> rules, ChromeUrl url, string resourceType = "main_frame")
    {
        DnrRule? best = null;
        foreach (var rule in rules)
        {
            if (!Matches(rule.Condition, url, resourceType))
                continue;
            if (best is null || rule.Priority > best.Priority || (rule.Priority == best.Priority && Rank(rule) > Rank(best)))
                best = rule;
        }
        return best;
    }

    /// <summary>Allow unless the winning rule blocks or redirects (no match = allow).</summary>
    public static DnrOutcome Evaluate(IEnumerable<DnrRule> rules, ChromeUrl url, string resourceType = "main_frame") =>
        Match(rules, url, resourceType)?.Action.Type is "block" or "redirect" ? DnrOutcome.Block : DnrOutcome.Allow;

    public static DnrOutcome Evaluate(IEnumerable<DnrRule> rules, string url, string resourceType = "main_frame") =>
        Evaluate(rules, ChromeUrl.Parse(url), resourceType);

    /// <summary>What Chrome navigates to for a redirect rule (regexSubstitution applied to the first match).</summary>
    public static string RedirectTarget(DnrRule rule, ChromeUrl url)
    {
        var condition = rule.Condition;
        var regex = Compile(condition.RegexFilter!, condition.IsUrlFilterCaseSensitive == true);
        var spec = url.Spec;
        var match = regex.Match(spec);
        var substituted = Regex.Replace(rule.Action.Redirect!.RegexSubstitution!, @"\\([0-9])", m => match.Groups[int.Parse(m.Groups[1].Value)].Value);
        return spec[..match.Index] + substituted + spec[(match.Index + match.Length)..];
    }

    private static bool Matches(DnrCondition condition, ChromeUrl url, string resourceType)
    {
        if (!condition.ResourceTypes.Contains(resourceType))
            return false;
        if (condition.UrlFilter is not null)
            throw new NotSupportedException("urlFilter is not modelled");
        if (condition.RequestDomains is { } domains && !domains.Any(d => DomainMatches(url.DomainHost, d)))
            return false;
        if (condition.ExcludedRequestDomains is { } excluded && excluded.Any(d => DomainMatches(url.DomainHost, d)))
            return false;
        return condition.RegexFilter is null || Compile(condition.RegexFilter, condition.IsUrlFilterCaseSensitive == true).IsMatch(url.Spec);
    }

    private static bool DomainMatches(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static Regex Compile(string pattern, bool caseSensitive) =>
        Regexes.GetOrAdd((pattern, caseSensitive), key =>
            new Regex(key.Item1.Replace("(?P<", "(?<", StringComparison.Ordinal),
                RegexOptions.CultureInvariant | (key.Item2 ? RegexOptions.None : RegexOptions.IgnoreCase)));

    private static int Rank(DnrRule rule) => rule.Action.Type switch
    {
        "allow" => 5,
        "allowAllRequests" => 4,
        "block" => 3,
        "upgradeScheme" => 2,
        "redirect" => 1,
        _ => 0,
    };
}

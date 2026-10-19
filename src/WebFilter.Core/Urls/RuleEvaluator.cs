using WebFilter.Core.Decisions;
using WebFilter.Core.Model;

namespace WebFilter.Core.Urls;

/// <summary>
/// Steps 5–8 of the decision order in MVP-PLAN §6 for a restricted member, on an already-normalized web URL.
/// Time, pause, homework and YouTube are decided by the engine before this is called.
/// </summary>
public static class RuleEvaluator
{
    /// <summary>
    /// <list type="number">
    /// <item>IP literal → <c>blocked_ip_literal</c>, unless an allow rule (member or global) matches that IP.</item>
    /// <item>Member rules: if any match, the most specific wins (tie → block).
    /// allow → <c>allowed_rule</c> (or <c>allowed_always</c> if the rule is always-allowed); block → <c>blocked_rule</c>.</item>
    /// <item>Global rules and the member's category domains together: most specific wins (tie → block).
    /// Category domains behave like domain rules; reasons <c>allowed_category</c> / <c>blocked_category</c>.</item>
    /// <item>Default: allowlist → <c>blocked_not_in_allowlist</c>; blocklist → <c>allowed_default</c>.</item>
    /// </list>
    /// RuleId in the result is the winning rule's id (null for categories and defaults).
    /// </summary>
    public static DecisionResult Evaluate(EffectiveUrlPolicy policy, NormalizedUrl url) => throw new NotImplementedException();

    /// <summary>True if an always-allowed rule (member or global) matches the URL; returns its id.</summary>
    public static bool MatchesAlwaysAllowed(EffectiveUrlPolicy policy, NormalizedUrl url, out string? ruleId) => throw new NotImplementedException();

    /// <summary>True if the URL's host is in the given category for this policy's category domains (used by homework mode).</summary>
    public static bool IsInCategory(IReadOnlyDictionary<string, IReadOnlyList<string>> categories, string category, NormalizedUrl url) =>
        throw new NotImplementedException();
}

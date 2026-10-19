using WebFilter.Core.Dnr;
using WebFilter.Core.Model;
using WebFilter.Core.Urls;

namespace WebFilter.Core.Engine;

/// <summary>
/// Checks a URL rule before it enters the settings: the pattern must be valid for its type and must compile to browser
/// rules as written (DNR regex budget, RE2 features). The compiler degrades rules that slip through, but only safely.
/// </summary>
public static class RuleValidator
{
    /// <summary>Why <paramref name="rule"/> cannot be used, or null when it is fine.</summary>
    public static string? Validate(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!PatternMatcher.TryCompile(rule.Pattern, rule.Type, out _, out var error))
            return error ?? $"'{rule.Pattern}' is not a valid {rule.Type.ToString().ToLowerInvariant()} pattern.";
        return DnrPatterns.LimitError(rule);
    }

    /// <exception cref="ArgumentException">The rule is not valid; the message says why.</exception>
    public static void EnsureValid(Rule rule)
    {
        if (Validate(rule) is { } error)
            throw new ArgumentException($"Rule '{rule.Id}': {error}", nameof(rule));
    }
}

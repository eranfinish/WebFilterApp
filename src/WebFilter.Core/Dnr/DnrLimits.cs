namespace WebFilter.Core.Dnr;

/// <summary>
/// declarativeNetRequest limits the compiler stays within (Chrome/Edge 121+; verified on Edge 156 in spike S3 and with
/// <c>isRegexSupported</c>). <see cref="DnrCompiler.Compile"/> throws <see cref="DnrCompileException"/> when a policy cannot fit.
/// </summary>
public static class DnrLimits
{
    /// <summary><c>MAX_NUMBER_OF_DYNAMIC_RULES</c>.</summary>
    public const int MaxDynamicRules = 30_000;

    /// <summary><c>MAX_NUMBER_OF_UNSAFE_DYNAMIC_RULES</c> — redirect rules are "unsafe", and every block rule is a redirect.</summary>
    public const int MaxUnsafeDynamicRules = 5_000;

    /// <summary><c>MAX_NUMBER_OF_SESSION_RULES</c>.</summary>
    public const int MaxSessionRules = 5_000;

    /// <summary><c>MAX_NUMBER_OF_UNSAFE_SESSION_RULES</c>.</summary>
    public const int MaxUnsafeSessionRules = 5_000;

    /// <summary>
    /// <c>MAX_NUMBER_OF_REGEX_RULES</c>. Checked conservatively against dynamic and session rules combined
    /// (every block rule carries a regexFilter, because the redirect needs <c>regexSubstitution</c>).
    /// </summary>
    public const int MaxRegexRules = 1_000;

    /// <summary>
    /// Longest canonical prefix-rule path (percent-encoded, including the leading '/') the compiler accepts.
    /// RE2 in Chrome caps each regex at 2 KB of compiled program; Edge 156 accepts about 90 path characters in the
    /// generated prefix regex, so 80 leaves a margin. Admin UI and access-request approval must stay below it.
    /// </summary>
    public const int MaxPrefixPathLength = 80;

    /// <summary>
    /// Most labels in a wildcard's base domain (<c>*.a.b.c</c> = 3). The subdomain-only regex repeats a label matcher
    /// once per label; Edge 156 accepts up to 9, so 8 leaves a margin.
    /// </summary>
    public const int MaxWildcardLabels = 8;
}

/// <summary>Thrown when a policy cannot be expressed within <see cref="DnrLimits"/>.</summary>
public sealed class DnrCompileException(string message) : Exception(message);

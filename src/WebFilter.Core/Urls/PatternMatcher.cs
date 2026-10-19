using System.Diagnostics.CodeAnalysis;
using WebFilter.Core.Model;

namespace WebFilter.Core.Urls;

/// <summary>
/// How specific a match is (README §5). Higher wins: Tier first (Prefix 4 &gt; ExactHost 3 &gt; Subdomain/Wildcard 2 &gt; Regex 1),
/// then the number of labels in the pattern host, then the prefix path length.
/// </summary>
public readonly record struct Specificity(int Tier, int Labels, int PathLength) : IComparable<Specificity>
{
    public const int RegexTier = 1, SubdomainTier = 2, ExactHostTier = 3, PrefixTier = 4;

    public int CompareTo(Specificity other) =>
        Tier != other.Tier ? Tier.CompareTo(other.Tier)
        : Labels != other.Labels ? Labels.CompareTo(other.Labels)
        : PathLength.CompareTo(other.PathLength);

    public static bool operator >(Specificity a, Specificity b) => a.CompareTo(b) > 0;
    public static bool operator <(Specificity a, Specificity b) => a.CompareTo(b) < 0;
    public static bool operator >=(Specificity a, Specificity b) => a.CompareTo(b) >= 0;
    public static bool operator <=(Specificity a, Specificity b) => a.CompareTo(b) <= 0;
}

/// <summary>A rule pattern compiled once and matched many times.</summary>
public abstract class CompiledPattern
{
    public abstract PatternType Type { get; }

    /// <summary>The pattern in canonical form (normalized host, lowercase path for prefix rules).</summary>
    public abstract string Canonical { get; }

    public abstract bool Matches(NormalizedUrl url, out Specificity specificity);
}

public static class PatternMatcher
{
    /// <summary>
    /// Compiles a rule pattern. Semantics (README §5):
    /// <list type="bullet">
    /// <item><b>Domain</b> — host equals the domain or is a subdomain of it. IP-literal domains match that IP only.</item>
    /// <item><b>Wildcard</b> <c>*.example.com</c> — subdomains only, never the apex.</item>
    /// <item><b>Prefix</b> <c>host/path</c> — host by domain rules; path case-insensitive, matched at a segment boundary
    /// (the next char after the prefix is end, '/', '?' or '#') unless the pattern ends with '/'. Query is ignored.</item>
    /// <item><b>Regex</b> — RE2-compatible (no lookaround, no backreferences), matched against <see cref="NormalizedUrl.Href"/>.</item>
    /// </list>
    /// </summary>
    public static bool TryCompile(string pattern, PatternType type, [NotNullWhen(true)] out CompiledPattern? compiled, out string? error) =>
        throw new NotImplementedException();

    public static CompiledPattern Compile(string pattern, PatternType type) =>
        TryCompile(pattern, type, out var compiled, out var error) ? compiled : throw new ArgumentException(error, nameof(pattern));
}

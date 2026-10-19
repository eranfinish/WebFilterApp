using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
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

    public override string ToString() => $"{Type}:{Canonical}";
}

public static class PatternMatcher
{
    private const RegexOptions RegexRuleOptions = RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const int MaxCachedCombinedRegexes = 256;
    private static readonly ConcurrentDictionary<string, Regex?> CombinedRegexes = new(StringComparer.Ordinal);

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
    /// <remarks>
    /// A prefix pattern may start with http:// or https:// (dropped: rules are scheme-independent); a query or fragment
    /// in it is dropped too, since queries never take part in prefix matching.
    /// </remarks>
    public static bool TryCompile(string pattern, PatternType type, [NotNullWhen(true)] out CompiledPattern? compiled, out string? error)
    {
        compiled = null;
        error = null;
        var text = pattern?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "The pattern is empty.";
            return false;
        }

        switch (type)
        {
            case PatternType.Domain:
                if (text.Contains('*'))
                {
                    error = $"'{text}' contains '*'. Use a wildcard pattern such as *.example.com.";
                    return false;
                }
                if (!UrlNormalizer.TryNormalizeHost(text, out var host, out var kind))
                {
                    error = $"'{text}' is not a valid domain or IP address.";
                    return false;
                }
                compiled = new DomainPattern(host, kind);
                return true;

            case PatternType.Wildcard:
                if (!text.StartsWith("*.", StringComparison.Ordinal) || text.IndexOf('*', 1) >= 0)
                {
                    error = $"'{text}' is not a wildcard pattern. Write it as *.example.com.";
                    return false;
                }
                if (!UrlNormalizer.TryNormalizeHost(text[2..], out var baseHost, out var baseKind) || baseKind != HostKind.Domain)
                {
                    error = $"'{text[2..]}' is not a valid domain.";
                    return false;
                }
                compiled = new WildcardPattern(baseHost);
                return true;

            case PatternType.Prefix:
                return TryCompilePrefix(text, out compiled, out error);

            case PatternType.Regex:
                try
                {
                    compiled = new RegexPattern(text, new Regex(text, RegexRuleOptions));
                    return true;
                }
                catch (NotSupportedException)
                {
                    error = "The regular expression uses a feature RE2 does not support (lookaround, backreferences, atomic groups or conditionals).";
                    return false;
                }
                catch (ArgumentException ex)
                {
                    error = $"Invalid regular expression: {ex.Message}";
                    return false;
                }

            default:
                error = $"Unknown pattern type {type}.";
                return false;
        }
    }

    public static CompiledPattern Compile(string pattern, PatternType type) =>
        TryCompile(pattern, type, out var compiled, out var error) ? compiled : throw new ArgumentException(error, nameof(pattern));

    private static bool TryCompilePrefix(string text, [NotNullWhen(true)] out CompiledPattern? compiled, out string? error)
    {
        compiled = null;
        error = null;
        foreach (var scheme in (ReadOnlySpan<string>)["http://", "https://"])
        {
            if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                text = text[scheme.Length..];
                break;
            }
        }

        var cut = text.IndexOfAny(['?', '#']);
        if (cut >= 0)
            text = text[..cut];

        var slash = text.IndexOfAny(['/', '\\']);
        var hostText = slash < 0 ? text : text[..slash];
        if (hostText.Contains('*'))
        {
            error = $"'{text}' contains '*'. Prefix patterns take a plain host.";
            return false;
        }
        if (!UrlNormalizer.TryNormalizeHost(hostText, out var host, out var kind))
        {
            error = $"'{hostText}' is not a valid domain or IP address.";
            return false;
        }

        var path = UrlNormalizer.NormalizePath(slash < 0 ? "" : text.AsSpan(slash)).ToLowerInvariant();
        compiled = new PrefixPattern(host, kind, path);
        return true;
    }

    /// <summary>
    /// One regex that matches when any of the given (already valid) regex rules matches, used as a prefilter.
    /// Each pattern is wrapped in its own group, so inline options stay scoped to it. Null if it cannot be built.
    /// </summary>
    internal static Regex? TryCombineRegexes(IEnumerable<CompiledPattern> patterns)
    {
        var combined = string.Join('|', patterns.Select(p => $"(?:{p.Canonical})"));
        if (CombinedRegexes.TryGetValue(combined, out var cached))
            return cached;

        Regex? regex;
        try
        {
            regex = new Regex(combined, RegexRuleOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            regex = null;
        }
        // Policies are rebuilt whenever rules change; most chunks stay the same, so keep recent ones (bounded).
        if (CombinedRegexes.Count >= MaxCachedCombinedRegexes)
            CombinedRegexes.Clear();
        CombinedRegexes[combined] = regex;
        return regex;
    }

    internal static int CountLabels(string host, HostKind kind) =>
        kind == HostKind.IPv6 ? 1 : host.AsSpan().Count('.') + 1;

    /// <summary>Domain-rule host match: equal, or (for domains) a subdomain at a label boundary. IPs match only themselves.</summary>
    internal static bool HostMatches(string patternHost, HostKind patternKind, NormalizedUrl url, out bool exact)
    {
        exact = false;
        if (url.HostKind != patternKind)
            return false;

        var host = url.Host;
        if (host.Length == patternHost.Length)
        {
            exact = string.Equals(host, patternHost, StringComparison.Ordinal);
            return exact;
        }
        return patternKind == HostKind.Domain
            && host.Length > patternHost.Length + 1
            && host[host.Length - patternHost.Length - 1] == '.'
            && host.EndsWith(patternHost, StringComparison.Ordinal);
    }
}

/// <summary>Base for patterns keyed by a host, so the evaluator can index them by host suffix.</summary>
internal abstract class HostPattern(string host, HostKind kind) : CompiledPattern
{
    public string Host { get; } = host;
    public HostKind HostKind { get; } = kind;
    public int Labels { get; } = PatternMatcher.CountLabels(host, kind);
}

internal sealed class DomainPattern(string host, HostKind kind) : HostPattern(host, kind)
{
    public override PatternType Type => PatternType.Domain;
    public override string Canonical => Host;

    public override bool Matches(NormalizedUrl url, out Specificity specificity)
    {
        if (!PatternMatcher.HostMatches(Host, HostKind, url, out var exact))
        {
            specificity = default;
            return false;
        }
        specificity = new Specificity(exact ? Specificity.ExactHostTier : Specificity.SubdomainTier, Labels, 0);
        return true;
    }
}

internal sealed class WildcardPattern(string baseHost) : HostPattern(baseHost, HostKind.Domain)
{
    public override PatternType Type => PatternType.Wildcard;
    public override string Canonical => "*." + Host;

    public override bool Matches(NormalizedUrl url, out Specificity specificity)
    {
        if (!PatternMatcher.HostMatches(Host, HostKind.Domain, url, out var exact) || exact)
        {
            specificity = default;
            return false;
        }
        specificity = new Specificity(Specificity.SubdomainTier, Labels, 0);
        return true;
    }
}

internal sealed class PrefixPattern(string host, HostKind kind, string path) : HostPattern(host, kind)
{
    /// <summary>Lowercase, starts with '/'.</summary>
    public string Path { get; } = path;

    public override PatternType Type => PatternType.Prefix;
    public override string Canonical => (HostKind == HostKind.IPv6 ? $"[{Host}]" : Host) + Path;

    public override bool Matches(NormalizedUrl url, out Specificity specificity)
    {
        specificity = default;
        if (!PatternMatcher.HostMatches(Host, HostKind, url, out _) || !PathMatches(url.Path))
            return false;
        specificity = new Specificity(Specificity.PrefixTier, Labels, Path.Length);
        return true;
    }

    private bool PathMatches(string urlPath)
    {
        if (!urlPath.StartsWith(Path, StringComparison.OrdinalIgnoreCase))
            return false;
        // Segment boundary: "/r/learn" matches "/r/learn" and "/r/learn/x", not "/r/learnmore". The query is not in Path.
        return Path[^1] == '/' || urlPath.Length == Path.Length || urlPath[Path.Length] == '/';
    }
}

internal sealed class RegexPattern(string source, Regex regex) : CompiledPattern
{
    public override PatternType Type => PatternType.Regex;
    public override string Canonical => source;

    public bool IsMatch(string href) => regex.IsMatch(href);

    public override bool Matches(NormalizedUrl url, out Specificity specificity)
    {
        var matched = regex.IsMatch(url.Href);
        specificity = matched ? new Specificity(Specificity.RegexTier, 0, 0) : default;
        return matched;
    }
}

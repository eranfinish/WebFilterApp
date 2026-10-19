using System.Text;
using System.Text.RegularExpressions;
using WebFilter.Core.Model;

namespace WebFilter.Core.Dnr;

/// <summary>
/// One rule pattern as a DNR condition at a specificity level (see <see cref="DnrPriorities"/>).
/// <see cref="Domain"/> goes into requestDomains (null for regex rules). <see cref="AllowRegex"/> is the regexFilter of an
/// allow rule (null = requestDomains alone); <see cref="BlockRegex"/> is the regexFilter of a block rule and always
/// matches the whole URL, because the redirect substitutes <c>\0</c>.
/// </summary>
internal sealed record DnrMatch(int Level, string? Domain, string? AllowRegex, string BlockRegex, bool? CaseSensitive);

internal static class DnrPatterns
{
    /// <summary>Whole URL; the shape proven in spike S3 for redirects carrying the original URL.</summary>
    public const string WholeUrl = "^(.*)$";

    /// <summary>Every http(s) URL (catch-all blocks).</summary>
    public const string AnyWebUrl = "^(https?://.*)$";

    // Canonical URLs as Chrome matches them: scheme://[userinfo@]host[:port]/path[?query][#fragment].
    // Userinfo is visible to DNR regexes (verified on Edge 156), so host matchers skip it explicitly.
    private const string SchemeAndUserinfo = "^[^:]+://(?:[^/@]*@)?";
    private const string PortAndRest = "(?::[0-9]*)?/.*";

    /// <summary>
    /// IP-literal hosts. Chrome serializes every IPv4 form (decimal, hex, octal, short) as dotted decimal and IPv6 in brackets.
    /// </summary>
    public const string IpLiteralHost = SchemeAndUserinfo + @"(?:[0-9]+(?:\.[0-9]+){3}|\[[0-9a-f:.]*\])" + PortAndRest;

    /// <summary>
    /// With requestDomains = [d] (d has k labels) the host is d or a subdomain; this regex keeps hosts with more than k labels,
    /// i.e. subdomains only. It does not name d, so all wildcard domains with k labels share one rule. Tolerates a trailing dot.
    /// </summary>
    public static string SubdomainsOnly(int k) => SchemeAndUserinfo + @"(?:[^/@:.]+\.){" + k + @"}[^/@:]+" + PortAndRest;

    /// <summary>With requestDomains = [d] (d has k labels): the host is d itself.</summary>
    public static string ExactHostOnly(int k) =>
        SchemeAndUserinfo + (k > 1 ? @"(?:[^/@:.]+\.){" + (k - 1) + "}" : "") + @"[^/@:.]+\.?" + PortAndRest;

    /// <summary>Compiles a URL rule. Returns null for patterns that cannot match anything (invalid input).</summary>
    public static DnrMatch? FromRule(Rule rule) => rule.Type switch
    {
        PatternType.Domain => FromDomain(rule.Pattern),
        PatternType.Wildcard => FromWildcard(rule),
        PatternType.Prefix => FromPrefix(rule),
        PatternType.Regex => FromRegex(rule.Pattern),
        _ => null,
    };

    /// <summary>A domain rule or category domain: the domain and all its subdomains (requestDomains semantics).</summary>
    public static DnrMatch? FromDomain(string pattern)
    {
        if (!DnrHostNormalizer.TryNormalize(pattern, out var host, out var kind))
            return null;
        return new DnrMatch(DnrPriorities.HostLevel(DnrHostNormalizer.Labels(host, kind)), host, null, WholeUrl, null);
    }

    public static bool IsIpLiteral(string pattern) =>
        DnrHostNormalizer.TryNormalize(pattern, out _, out var kind) && kind != DnrHostKind.Domain;

    private static DnrMatch? FromWildcard(Rule rule)
    {
        var pattern = rule.Pattern.Trim();
        if (!pattern.StartsWith("*.", StringComparison.Ordinal) ||
            !DnrHostNormalizer.TryNormalize(pattern[2..], out var host, out var kind) || kind != DnrHostKind.Domain)
            return null;
        var labels = DnrHostNormalizer.Labels(host, kind);
        if (labels > DnrLimits.MaxWildcardLabels)
            throw new DnrCompileException(
                $"Rule '{rule.Id}': wildcard '{rule.Pattern}' has {labels} labels; at most {DnrLimits.MaxWildcardLabels} fit in a DNR regex.");
        var regex = SubdomainsOnly(labels);
        return new DnrMatch(DnrPriorities.HostLevel(labels), host, regex, regex, null);
    }

    private static DnrMatch? FromPrefix(Rule rule)
    {
        var pattern = rule.Pattern.Trim();
        if (pattern.Contains("://", StringComparison.Ordinal))
            return null;
        var cut = pattern.IndexOfAny(['?', '#']);
        if (cut >= 0)
            pattern = pattern[..cut];
        var slash = pattern.IndexOf('/');
        var hostPart = slash < 0 ? pattern : pattern[..slash];
        if (hostPart.Contains('*') || !DnrHostNormalizer.TryNormalize(hostPart, out var host, out var kind))
            return null;
        var path = CanonicalPath(slash < 0 ? "/" : pattern[slash..]);
        if (path.Length > DnrLimits.MaxPrefixPathLength)
            throw new DnrCompileException(
                $"Rule '{rule.Id}': prefix '{rule.Pattern}' has a {path.Length}-character path; at most {DnrLimits.MaxPrefixPathLength} fit in a DNR regex.");

        var level = DnrPriorities.PrefixLevel(DnrHostNormalizer.Labels(host, kind), path.Length);
        if (path == "/")
            return new DnrMatch(level, host, null, WholeUrl, null);

        // requestDomains checks the host; the regex checks the path, which starts at the first '/' after "://"
        // (userinfo and host never contain a raw '/'). DNR regexes are case-insensitive unless flagged; false is explicit
        // because Chrome before 118 defaulted to case-sensitive. Chrome does not decode %XX in paths, so "/r/%66unny" is not
        // "/r/funny" to DNR (known gap; the regex budget leaves no room for per-character alternatives).
        var regex = "^[^:]+://[^/]+" + Re2Escape(path) + (path.EndsWith('/') ? ".*" : "(?:[/?#].*)?$");
        return new DnrMatch(level, host, regex, regex, false);
    }

    /// <summary>
    /// A user regex, case-insensitive like the evaluator (a parent's "porn" must also catch "PORN"). Known gaps against the evaluator: Chrome keeps a
    /// trailing host dot ("host./") and its own percent-encoding in the URL the regex sees, and a regex too large for RE2's 2 KB
    /// program limit in Chrome makes the whole rule update fail — the extension should check <c>isRegexSupported</c> first.
    /// </summary>
    private static DnrMatch? FromRegex(string pattern)
    {
        if (!IsRe2Compatible(pattern))
            return null;
        var regex = AllowUserinfo(AllowFragment(pattern));
        // A block rule must match the whole URL so that \0 carries it to the block page.
        var block = regex.StartsWith('^') && !regex.Contains('|') ? $"(?:{regex}).*" : $"^.*?(?:{regex}).*";
        return new DnrMatch(DnrPriorities.RegexLevel, null, regex, block, false);
    }

    /// <summary>
    /// The prefix path as Chrome serializes a matching URL path: '\' → '/', dot segments resolved, characters Chrome
    /// escapes in paths percent-encoded (UTF-8), ASCII letters lowercased (matching is case-insensitive anyway).
    /// </summary>
    internal static string CanonicalPath(string path)
    {
        path = path.Replace('\\', '/');
        var segments = path.Split('/');
        var output = new List<string>();
        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i];
            var last = i == segments.Length - 1;
            if (IsDotSegment(segment, 1))
            {
                if (last)
                    output.Add("");
            }
            else if (IsDotSegment(segment, 2))
            {
                if (output.Count > 0)
                    output.RemoveAt(output.Count - 1);
                if (last)
                    output.Add("");
            }
            else
            {
                output.Add(segment);
            }
        }

        var sb = new StringBuilder("/");
        sb.Append(string.Join('/', output));
        var result = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(sb.ToString()))
        {
            var c = (char)b;
            if (b >= 0x80 || b <= 0x20 || b == 0x7F || c is '"' or '<' or '>' or '`' or '{' or '}' or '|' or '^')
                result.Append('%').Append(b.ToString("X2"));
            else
                result.Append(char.ToLowerInvariant(c));
        }
        return result.ToString();
    }

    private static bool IsDotSegment(string segment, int dots)
    {
        var s = segment.Replace("%2e", ".", StringComparison.OrdinalIgnoreCase);
        return s.Length == dots && s.All(c => c == '.');
    }

    internal static string Re2Escape(string literal)
    {
        var sb = new StringBuilder(literal.Length * 2);
        foreach (var c in literal)
        {
            if (@"\.+*?()|[]{}^$".Contains(c))
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static readonly string[] SchemePrefixes =
    [
        "^https?", "^http", "^https", "^(https?)", "^(?:https?)", "^(http|https)", "^(?:http|https)",
        "^(https|http)", "^(?:https|http)", "^[a-z]+", "^[a-z]*", "^[^:]+", "^[^:]*", @"^\w+",
    ];

    /// <summary>
    /// Chrome's URL carries userinfo; the evaluator's normalized URL does not. For the common anchored forms
    /// (<c>^https?://</c> …) an optional userinfo is allowed right after "://", so "https://x@host/" cannot slip past a
    /// regex block. The group can only match text ending in '@' before the first '/', so other URLs are unaffected.
    /// </summary>
    private static string AllowUserinfo(string regex)
    {
        foreach (var separator in new[] { "://", @":\/\/" })
        {
            var at = regex.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0 && SchemePrefixes.Contains(regex[..at], StringComparer.Ordinal))
                return regex[..(at + separator.Length)] + "(?:[^/@]*@)?" + regex[(at + separator.Length)..];
        }
        return regex;
    }

    /// <summary>Chrome's URL carries the fragment; a trailing <c>$</c> also accepts one, so "page#x" cannot slip past.</summary>
    private static string AllowFragment(string regex)
    {
        if (!regex.EndsWith('$'))
            return regex;
        var backslashes = 0;
        for (var i = regex.Length - 2; i >= 0 && regex[i] == '\\'; i--)
            backslashes++;
        return backslashes % 2 == 0 ? regex[..^1] + "(?:#.*)?$" : regex;
    }

    /// <summary>
    /// Rejects what RE2 (and therefore DNR) does not accept — lookaround, backreferences, atomic groups, possessive
    /// quantifiers — and anything that is not a valid regex at all. One invalid regexFilter would make Chrome reject
    /// the whole rule update, so such a rule is skipped (it cannot match in the evaluator either).
    /// </summary>
    internal static bool IsRe2Compatible(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return false;
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            var next = i + 1 < pattern.Length ? pattern[i + 1] : '\0';
            if (c == '\\')
            {
                if (next is '\0' or (>= '1' and <= '9') or 'k' or 'g' or 'Z' or 'G')
                    return false;
                i++;
                continue;
            }
            if (inClass)
            {
                if (c == ']')
                    inClass = false;
                continue;
            }
            switch (c)
            {
                case '[':
                    inClass = true;
                    if (next == '^')
                        i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == ']')
                        i++;
                    break;
                case '(' when next == '?':
                    var kind = i + 2 < pattern.Length ? pattern[i + 2] : '\0';
                    var after = i + 3 < pattern.Length ? pattern[i + 3] : '\0';
                    if (kind is '=' or '!' or '>' or '#' || (kind == '<' && after is '=' or '!'))
                        return false;
                    break;
                case '*' or '+' or '?' or '}' when next == '+':
                    return false;
            }
        }
        if (inClass)
            return false;
        try
        {
            _ = new Regex(pattern.Replace("(?P<", "(?<", StringComparison.Ordinal), RegexOptions.CultureInvariant);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

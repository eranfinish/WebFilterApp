using System.Globalization;
using System.Text.RegularExpressions;
using WebFilter.Admin.Resources;

namespace WebFilter.Admin.ViewModels;

/// <summary>
/// Checks a URL rule pattern before it is sent, with plain-language messages. Mirrors the engine's pattern rules
/// (tests/simulation/README.md §5, Core's PatternMatcher): <c>domain</c> = a host (subdomains included, no '*'),
/// <c>wildcard</c> = <c>*.example.com</c> (subdomains only), <c>prefix</c> = <c>host/path</c>, <c>regex</c> = RE2-compatible.
/// </summary>
internal static class RulePatternValidator
{
    public static readonly string[] Types = ["domain", "wildcard", "prefix", "regex"];

    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };

    /// <summary>Validates <paramref name="input"/> for <paramref name="type"/>; on success returns the pattern to store.</summary>
    public static bool TryNormalize(string? input, string type, out string pattern, out string? error)
    {
        pattern = "";
        error = null;
        var text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = Strings.Validation_Empty;
            return false;
        }

        switch (type)
        {
            case "domain":
                // A pasted site address ("https://example.com/") is accepted as its host.
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    if (uri.AbsolutePath != "/" || uri.Query.Length > 0)
                    {
                        error = Strings.Validation_DomainHasPath;
                        return false;
                    }
                    text = uri.Host;
                }
                if (text.Contains('*'))
                {
                    error = Strings.Validation_DomainStar;
                    return false;
                }
                if (text.Contains('/'))
                {
                    error = Strings.Validation_DomainHasPath;
                    return false;
                }
                if (!IsHost(text, allowIp: true))
                {
                    error = Texts.Format(Strings.Validation_DomainInvalid, Texts.Ltr(text));
                    return false;
                }
                pattern = text.TrimEnd('.').ToLowerInvariant();
                return true;

            case "wildcard":
                if (!text.StartsWith("*.", StringComparison.Ordinal) || text.IndexOf('*', 1) >= 0 || text.Contains('/'))
                {
                    error = Strings.Validation_WildcardForm;
                    return false;
                }
                if (!IsHost(text[2..], allowIp: false))
                {
                    error = Texts.Format(Strings.Validation_DomainInvalid, Texts.Ltr(text[2..]));
                    return false;
                }
                pattern = text.TrimEnd('.').ToLowerInvariant();
                return true;

            case "prefix":
                foreach (var scheme in (ReadOnlySpan<string>)["http://", "https://"])
                {
                    if (text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                    {
                        text = text[scheme.Length..];
                        break;
                    }
                }
                var slash = text.IndexOf('/');
                if (slash <= 0 || text.Contains('*'))
                {
                    error = Strings.Validation_PrefixForm;
                    return false;
                }
                if (!IsHost(text[..slash], allowIp: true))
                {
                    error = Texts.Format(Strings.Validation_DomainInvalid, Texts.Ltr(text[..slash]));
                    return false;
                }
                pattern = text[..slash].ToLowerInvariant() + text[slash..];
                return true;

            case "regex":
                try
                {
                    // NonBacktracking rejects what RE2 cannot do (lookaround, backreferences, atomic groups, conditionals).
                    _ = new Regex(text, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
                }
                catch (NotSupportedException)
                {
                    error = Strings.Validation_RegexUnsupported;
                    return false;
                }
                catch (ArgumentException)
                {
                    error = Strings.Validation_RegexInvalid;
                    return false;
                }
                pattern = text;
                return true;

            default:
                throw new ArgumentException($"Unknown rule type '{type}'.", nameof(type));
        }
    }

    /// <summary>A DNS name with at least two labels (IDN allowed), or an IP address when <paramref name="allowIp"/>.</summary>
    private static bool IsHost(string text, bool allowIp)
    {
        var host = text.TrimEnd('.');
        if (host.Length is 0 or > 253 || host.Any(char.IsWhiteSpace)) return false;
        if (allowIp && (Uri.CheckHostName(host) is UriHostNameType.IPv4 || Uri.CheckHostName(host.Trim('[', ']')) is UriHostNameType.IPv6)) return true;
        string ascii;
        try
        {
            ascii = Idn.GetAscii(host);
        }
        catch (ArgumentException)
        {
            return false;
        }
        return ascii.Contains('.') && Uri.CheckHostName(ascii) == UriHostNameType.Dns && !ascii.Split('.').All(label => label.All(char.IsAsciiDigit));
    }
}

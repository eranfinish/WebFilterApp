using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WebFilter.Core.Tests.Dnr;

/// <summary>
/// A compact model of how Chrome canonicalizes an http(s) URL before declarativeNetRequest matches it
/// (behavior checked on Edge 156 with <c>testMatchOutcome</c>): lowercase scheme and host, percent-decoded host,
/// IDN → punycode (UTS-46 nontransitional), IPv4 number forms → dotted decimal, IPv6 compressed in brackets,
/// trailing host dot kept, default port dropped, userinfo kept (empty parts dropped), '\' → '/', dot segments resolved,
/// Chrome's percent-encode sets for userinfo / path / query / fragment, and the fragment kept.
/// Independent of the compiler's own host normalizer on purpose.
/// </summary>
internal sealed record ChromeUrl(string Scheme, string Userinfo, string Host, int? Port, string Path, string? Query, string? Fragment)
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = true, UseStd3AsciiRules = false };

    /// <summary>The host as requestDomains compares it (no trailing dot).</summary>
    public string DomainHost => Host.Length > 1 && Host.EndsWith('.') ? Host[..^1] : Host;

    public bool IsIpLiteral => Host.StartsWith('[') || IsIPv4(Host);

    /// <summary>The URL string regexFilter sees.</summary>
    public string Spec =>
        $"{Scheme}://{(Userinfo.Length > 0 ? Userinfo + "@" : "")}{Host}{(Port is null ? "" : ":" + Port)}{Path}" +
        $"{(Query is null ? "" : "?" + Query)}{(Fragment is null ? "" : "#" + Fragment)}";

    /// <summary>The evaluator's normalized URL: no userinfo, no fragment, no trailing host dot.</summary>
    public string Href => $"{Scheme}://{DomainHost}{(Port is null ? "" : ":" + Port)}{Path}{(Query is null ? "" : "?" + Query)}";

    public override string ToString() => Spec;

    public static ChromeUrl Parse(string input) =>
        TryParse(input, out var url, out var error) ? url : throw new FormatException($"{input}: {error}");

    public static bool TryParse(string input, out ChromeUrl url, out string error)
    {
        url = null!;
        error = "";
        var s = new string(input.Trim('\0', ' ', '\t', '\n', '\r', '\f').Where(c => c is not ('\t' or '\n' or '\r')).ToArray());
        var colon = s.IndexOf(':');
        if (colon <= 0 || !char.IsAsciiLetter(s[0]) || s[..colon].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.')))
            return Fail("no scheme", out error);
        var scheme = s[..colon].ToLowerInvariant();
        if (scheme is not ("http" or "https"))
            return Fail("not an http(s) URL", out error);

        var rest = s[(colon + 1)..].TrimStart('/', '\\');
        var authorityEnd = rest.IndexOfAny(['/', '\\', '?', '#']);
        if (authorityEnd < 0)
            authorityEnd = rest.Length;
        var authority = rest[..authorityEnd];
        var tail = rest[authorityEnd..];

        var at = authority.LastIndexOf('@');
        var userinfo = at < 0 ? "" : CanonicalUserinfo(authority[..at]);
        var hostPort = at < 0 ? authority : authority[(at + 1)..];

        string rawHost;
        string? rawPort = null;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close < 0)
                return Fail("unterminated IPv6", out error);
            rawHost = hostPort[..(close + 1)];
            var after = hostPort[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':')
                    return Fail("junk after IPv6", out error);
                rawPort = after[1..];
            }
        }
        else
        {
            var portColon = hostPort.LastIndexOf(':');
            rawHost = portColon < 0 ? hostPort : hostPort[..portColon];
            rawPort = portColon < 0 ? null : hostPort[(portColon + 1)..];
        }

        if (!TryCanonicalHost(rawHost, out var host, out error))
            return false;

        int? port = null;
        if (!string.IsNullOrEmpty(rawPort))
        {
            if (!rawPort.All(char.IsAsciiDigit) || !int.TryParse(rawPort, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p > 65535)
                return Fail("bad port", out error);
            port = (scheme, p) is ("http", 80) or ("https", 443) ? null : p;
        }

        string? fragment = null, query = null;
        var hash = tail.IndexOf('#');
        if (hash >= 0)
        {
            fragment = Encode(tail[(hash + 1)..], "\"<>`");
            tail = tail[..hash];
        }
        var question = tail.IndexOf('?');
        if (question >= 0)
        {
            query = Encode(tail[(question + 1)..], "\"<>'");
            tail = tail[..question];
        }

        url = new ChromeUrl(scheme, userinfo, host, port, CanonicalPath(tail), query, fragment);
        return true;
    }

    private static string CanonicalUserinfo(string userinfo)
    {
        const string set = "\"<>`{}|^[]\\';=@/?#";
        var colon = userinfo.IndexOf(':');
        var user = Encode(colon < 0 ? userinfo : userinfo[..colon], set);
        var password = colon < 0 ? "" : Encode(userinfo[(colon + 1)..], set + ":");
        return password.Length > 0 ? $"{user}:{password}" : user;
    }

    private static string CanonicalPath(string path)
    {
        path = path.Replace('\\', '/');
        if (path.Length == 0)
            return "/";
        var segments = path.Split('/');
        var output = new List<string>();
        for (var i = 1; i < segments.Length; i++)
        {
            var dots = segments[i].Replace("%2e", ".", StringComparison.OrdinalIgnoreCase);
            var last = i == segments.Length - 1;
            if (dots == ".")
            {
                if (last)
                    output.Add("");
            }
            else if (dots == "..")
            {
                if (output.Count > 0)
                    output.RemoveAt(output.Count - 1);
                if (last)
                    output.Add("");
            }
            else
            {
                output.Add(segments[i]);
            }
        }
        return Encode("/" + string.Join('/', output), "\"<>`{}|^");
    }

    // Percent-encodes C0 controls, space, DEL, non-ASCII (as UTF-8) and the given ASCII characters; keeps '%' as is.
    private static string Encode(string text, string set)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            if (b <= 0x20 || b >= 0x7F || set.Contains((char)b))
                sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            else
                sb.Append((char)b);
        }
        return sb.ToString();
    }

    private static bool TryCanonicalHost(string raw, out string host, out string error)
    {
        host = "";
        error = "";
        if (raw.StartsWith('['))
        {
            if (!raw.EndsWith(']') || !IPAddress.TryParse(raw[1..^1], out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6 || raw.Contains('%'))
                return Fail("bad IPv6", out error);
            host = "[" + SerializeIPv6(v6.GetAddressBytes()) + "]";
            return true;
        }

        if (!TryPercentDecode(raw, out var decoded))
            return Fail("bad percent-encoding in host", out error);
        var trailingDot = decoded.Length > 1 && decoded.EndsWith('.');
        var bare = trailingDot ? decoded[..^1] : decoded;
        if (bare.Length == 0)
            return Fail("empty host", out error);
        if (bare.Any(c => c > 0x7F))
        {
            try
            {
                bare = Idn.GetAscii(bare.ToLowerInvariant());
            }
            catch (ArgumentException)
            {
                return Fail("IDN conversion failed", out error);
            }
        }
        bare = bare.ToLowerInvariant();
        if (bare.Any(c => c <= 0x20 || c == 0x7F || "#%/:<>?@[\\]^|".Contains(c)))
            return Fail("forbidden host code point", out error);

        var labels = bare.Split('.');
        var last = labels[^1];
        var endsInNumber = last.All(char.IsAsciiDigit) && last.Length > 0 ||
                           last.Length >= 2 && last[0] == '0' && last[1] == 'x' && last[2..].All(char.IsAsciiHexDigit);
        if (endsInNumber)
        {
            if (!TryIPv4(labels, out host))
                return Fail("invalid IPv4", out error);
            return true;
        }
        host = trailingDot ? bare + "." : bare;
        return true;
    }

    private static bool TryIPv4(string[] parts, out string host)
    {
        host = "";
        if (parts.Length > 4 || parts.Any(p => p.Length == 0))
            return false;
        var numbers = new List<ulong>();
        foreach (var part in parts)
        {
            var (digits, radix) = part.StartsWith("0x") ? (part[2..], 16) : part.Length > 1 && part[0] == '0' ? (part[1..], 8) : (part, 10);
            ulong value = 0;
            foreach (var c in digits)
            {
                var d = char.IsAsciiDigit(c) ? c - '0' : char.IsAsciiHexDigit(c) ? char.ToLowerInvariant(c) - 'a' + 10 : 99;
                if (d >= radix)
                    return false;
                value = value * (ulong)radix + (ulong)d;
                if (value > uint.MaxValue)
                    return false;
            }
            numbers.Add(value);
        }
        if (numbers.Take(numbers.Count - 1).Any(n => n > 255) || numbers[^1] >= 1UL << (8 * (5 - numbers.Count)))
            return false;
        var ip = numbers[^1];
        for (var i = 0; i < numbers.Count - 1; i++)
            ip += numbers[i] << (8 * (3 - i));
        host = string.Join('.', new[] { ip >> 24, (ip >> 16) & 255, (ip >> 8) & 255, ip & 255 });
        return true;
    }

    private static bool IsIPv4(string host)
    {
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));
    }

    private static string SerializeIPv6(byte[] bytes)
    {
        var pieces = Enumerable.Range(0, 8).Select(i => (bytes[2 * i] << 8) | bytes[2 * i + 1]).ToArray();
        int bestStart = -1, bestLength = 1;
        for (var i = 0; i < 8; i++)
        {
            var j = i;
            while (j < 8 && pieces[j] == 0)
                j++;
            if (j - i > bestLength)
                (bestStart, bestLength) = (i, j - i);
        }
        if (bestStart < 0)
            return string.Join(':', pieces.Select(p => p.ToString("x", CultureInfo.InvariantCulture)));
        var head = string.Join(':', pieces[..bestStart].Select(p => p.ToString("x", CultureInfo.InvariantCulture)));
        var tail = string.Join(':', pieces[(bestStart + bestLength)..].Select(p => p.ToString("x", CultureInfo.InvariantCulture)));
        return head + "::" + tail;
    }

    private static bool TryPercentDecode(string s, out string decoded)
    {
        decoded = s;
        if (!s.Contains('%'))
            return true;
        var bytes = new List<byte>();
        for (var i = 0; i < s.Length;)
        {
            if (s[i] == '%')
            {
                if (i + 2 >= s.Length || !char.IsAsciiHexDigit(s[i + 1]) || !char.IsAsciiHexDigit(s[i + 2]))
                    return false;
                bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16));
                i += 3;
            }
            else
            {
                var next = s.IndexOf('%', i);
                var end = next < 0 ? s.Length : next;
                bytes.AddRange(Encoding.UTF8.GetBytes(s[i..end]));
                i = end;
            }
        }
        try
        {
            decoded = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}

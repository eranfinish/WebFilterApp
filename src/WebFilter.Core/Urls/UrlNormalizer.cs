using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace WebFilter.Core.Urls;

/// <summary>
/// A WHATWG-style URL parser reduced to what filtering needs (http/https only). The host and path rules follow the
/// URL Standard so that we see the same host the browser connects to.
/// </summary>
public static class UrlNormalizer
{
    /// <summary>True for http and https URLs. Other schemes (chrome:, edge:, about:, file:, data:) are not filtered by us.</summary>
    /// <remarks>Only the scheme is checked: an unparseable http(s) URL is still a web URL, so callers can fail closed on it.</remarks>
    public static bool IsWebUrl(string input) =>
        input is not null && TryReadScheme(Clean(input), out var scheme, out _) && IsWebScheme(scheme);

    /// <summary>Normalizes an http/https URL. Returns false for other schemes or unparseable input.</summary>
    public static bool TryNormalize(string input, [NotNullWhen(true)] out NormalizedUrl? url)
    {
        url = null;
        if (input is null)
            return false;

        var s = Clean(input);
        if (!TryReadScheme(s, out var scheme, out var i) || !IsWebScheme(scheme))
            return false;

        // Special schemes ignore any run of slashes and backslashes before the authority ("http:\\host", "http:host").
        while (i < s.Length && s[i] is '/' or '\\')
            i++;

        var authorityLength = s.AsSpan(i).IndexOfAny("/\\?#");
        var authorityEnd = authorityLength < 0 ? s.Length : i + authorityLength;
        var authority = s.AsSpan(i, authorityEnd - i);

        // Everything up to the last '@' is userinfo; the host is what follows it.
        var at = authority.LastIndexOf('@');
        if (at >= 0)
            authority = authority[(at + 1)..];

        var colon = FindPortSeparator(authority);
        var hostPart = colon < 0 ? authority : authority[..colon];
        int? port = null;
        if (colon >= 0 && colon + 1 < authority.Length)
        {
            if (!TryParsePort(authority[(colon + 1)..], out var value))
                return false;
            if (value != DefaultPort(scheme))
                port = value;
        }

        if (!TryParseHost(hostPart, out var host, out var kind))
            return false;

        var rest = s.AsSpan(authorityEnd);
        var hash = rest.IndexOf('#');
        if (hash >= 0)
            rest = rest[..hash];
        var question = rest.IndexOf('?');
        var path = NormalizePath(question < 0 ? rest : rest[..question]);
        var query = question < 0 ? "" : PercentEncode(rest[(question + 1)..], inQuery: true);

        url = new NormalizedUrl(scheme, host, kind, port, path, query);
        return true;
    }

    /// <summary>
    /// Normalizes a host as written in a rule pattern ("Bücher.Example", "192.168.1.50", "[::1]") to the same form
    /// <see cref="TryNormalize"/> produces. Returns false if it is not a valid host.
    /// </summary>
    public static bool TryNormalizeHost(string host, [NotNullWhen(true)] out string? normalized, out HostKind kind)
    {
        normalized = null;
        kind = HostKind.Domain;
        if (host is null)
            return false;

        var span = host.AsSpan().Trim();
        // Also accept an IPv6 address typed without brackets ("::1").
        if (span.Length > 0 && span[0] != '[' && span.Contains(':'))
        {
            if (!TryParseIPv6(span, out var pieces))
                return false;
            normalized = SerializeIPv6(pieces);
            kind = HostKind.IPv6;
            return true;
        }
        return TryParseHost(span, out normalized, out kind);
    }

    /// <summary>
    /// Path normalization shared with prefix patterns: backslashes act as '/', dot segments (also percent-encoded) are
    /// resolved, characters a browser would percent-encode are encoded, existing escapes are kept. Empty → "/".
    /// </summary>
    internal static string NormalizePath(ReadOnlySpan<char> raw)
    {
        if (raw.Length == 0)
            return "/";

        var segments = new List<string>();
        var start = raw[0] is '/' or '\\' ? 1 : 0;
        for (var k = start; k <= raw.Length; k++)
        {
            if (k < raw.Length && raw[k] is not ('/' or '\\'))
                continue;

            var segment = raw[start..k];
            var isLast = k == raw.Length;
            if (IsDoubleDotSegment(segment))
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                if (isLast)
                    segments.Add("");
            }
            else if (IsSingleDotSegment(segment))
            {
                if (isLast)
                    segments.Add("");
            }
            else
            {
                segments.Add(PercentEncode(segment, inQuery: false));
            }
            start = k + 1;
        }
        return "/" + string.Join('/', segments);
    }

    // ---- scheme ----

    private static bool IsWebScheme(string scheme) => scheme is "http" or "https";

    private static int DefaultPort(string scheme) => scheme == "https" ? 443 : 80;

    /// <summary>Strips leading/trailing C0 controls and spaces and removes tabs and newlines, like the URL Standard.</summary>
    private static string Clean(string input)
    {
        int start = 0, end = input.Length;
        while (start < end && input[start] <= ' ')
            start++;
        while (end > start && input[end - 1] <= ' ')
            end--;

        var span = input.AsSpan(start, end - start);
        if (span.IndexOfAny('\t', '\n', '\r') < 0)
            return span.Length == input.Length ? input : span.ToString();

        var sb = new StringBuilder(span.Length);
        foreach (var c in span)
        {
            if (c is not ('\t' or '\n' or '\r'))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool TryReadScheme(string s, out string scheme, out int afterColon)
    {
        scheme = "";
        afterColon = 0;
        if (s.Length == 0 || !char.IsAsciiLetter(s[0]))
            return false;

        for (var i = 1; i < s.Length; i++)
        {
            var c = s[i];
            if (c == ':')
            {
                scheme = s[..i].ToLowerInvariant();
                afterColon = i + 1;
                return true;
            }
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
                return false;
        }
        return false;
    }

    // ---- authority ----

    /// <summary>The first ':' outside brackets separates host and port.</summary>
    private static int FindPortSeparator(ReadOnlySpan<char> authority)
    {
        var inBrackets = false;
        for (var k = 0; k < authority.Length; k++)
        {
            switch (authority[k])
            {
                case '[': inBrackets = true; break;
                case ']': inBrackets = false; break;
                case ':' when !inBrackets: return k;
            }
        }
        return -1;
    }

    private static bool TryParsePort(ReadOnlySpan<char> text, out int port)
    {
        port = 0;
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
                return false;
            port = port * 10 + (c - '0');
            if (port > 65535)
                return false;
        }
        return true;
    }

    private static bool TryParseHost(ReadOnlySpan<char> input, [NotNullWhen(true)] out string? host, out HostKind kind)
    {
        host = null;
        kind = HostKind.Domain;
        if (input.Length == 0)
            return false;

        if (input[0] == '[')
        {
            if (input.Length < 2 || input[^1] != ']' || !TryParseIPv6(input[1..^1], out var pieces))
                return false;
            host = SerializeIPv6(pieces);
            kind = HostKind.IPv6;
            return true;
        }

        var decoded = PercentDecode(input);
        var strippedDot = false;
        if (decoded.EndsWith('.'))
        {
            decoded = decoded[..^1];
            strippedDot = true;
        }

        string ascii;
        if (Ascii.IsValid(decoded))
        {
            ascii = decoded.ToLowerInvariant();
        }
        else
        {
            try
            {
                ascii = new IdnMapping().GetAscii(decoded).ToLowerInvariant();
            }
            catch (ArgumentException)
            {
                return false;
            }
            // UTS46 maps ideographic and fullwidth full stops to '.'.
            if (!strippedDot && ascii.EndsWith('.'))
                ascii = ascii[..^1];
        }

        if (ascii.Length == 0)
            return false;
        foreach (var c in ascii)
        {
            if (IsForbiddenDomainCodePoint(c))
                return false;
        }

        if (EndsInNumber(ascii))
        {
            if (!TryParseIPv4(ascii, out var address))
                return false;
            host = $"{address >> 24}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}";
            kind = HostKind.IPv4;
            return true;
        }

        host = ascii;
        return true;
    }

    private static bool IsForbiddenDomainCodePoint(char c) =>
        c <= 0x20 || c == 0x7F || c is '#' or '%' or '/' or ':' or '<' or '>' or '?' or '@' or '[' or '\\' or ']' or '^' or '|';

    // ---- IPv4 (URL Standard: "ends in a number" and the IPv4 parser) ----

    private static bool EndsInNumber(string host)
    {
        var span = host.AsSpan();
        if (span[^1] == '.')
        {
            if (span.Length == 1)
                return false;
            span = span[..^1];
        }
        var last = span[(span.LastIndexOf('.') + 1)..];
        if (last.Length == 0)
            return false;
        if (!last.ContainsAnyExceptInRange('0', '9'))
            return true;
        return last.Length >= 2 && last[0] == '0' && last[1] is 'x' or 'X' && !last[2..].ContainsAnyExcept("0123456789abcdefABCDEF");
    }

    private static bool TryParseIPv4(string host, out uint address)
    {
        address = 0;
        var parts = host.Split('.');
        var count = parts.Length;
        if (count > 1 && parts[^1].Length == 0)
            count--;
        if (count > 4)
            return false;

        Span<ulong> numbers = stackalloc ulong[4];
        for (var i = 0; i < count; i++)
        {
            if (!TryParseIPv4Number(parts[i], out numbers[i]))
                return false;
        }
        for (var i = 0; i < count - 1; i++)
        {
            if (numbers[i] > 255)
                return false;
        }
        if (numbers[count - 1] >= 1UL << (8 * (5 - count)))
            return false;

        var result = numbers[count - 1];
        for (var i = 0; i < count - 1; i++)
            result += numbers[i] << (8 * (3 - i));
        address = (uint)result;
        return true;
    }

    private static bool TryParseIPv4Number(ReadOnlySpan<char> text, out ulong value)
    {
        value = 0;
        if (text.Length == 0)
            return false;

        var radix = 10;
        if (text.Length >= 2 && text[0] == '0' && text[1] is 'x' or 'X')
        {
            radix = 16;
            text = text[2..];
        }
        else if (text.Length >= 2 && text[0] == '0')
        {
            radix = 8;
            text = text[1..];
        }

        foreach (var c in text)
        {
            var digit = HexValue(c);
            if (digit < 0 || digit >= radix)
                return false;
            // Saturate: anything this large fails the range checks anyway.
            value = value > (1UL << 40) ? value : value * (ulong)radix + (ulong)digit;
        }
        return true;
    }

    // ---- IPv6 (URL Standard IPv6 parser and serializer) ----

    private static bool TryParseIPv6(ReadOnlySpan<char> input, out ushort[] address)
    {
        address = new ushort[8];
        var pieceIndex = 0;
        int? compress = null;
        var p = 0;

        if (At(input, p) == ':')
        {
            if (At(input, p + 1) != ':')
                return false;
            p += 2;
            pieceIndex++;
            compress = pieceIndex;
        }

        while (p < input.Length)
        {
            if (pieceIndex == 8)
                return false;
            if (input[p] == ':')
            {
                if (compress is not null)
                    return false;
                p++;
                pieceIndex++;
                compress = pieceIndex;
                continue;
            }

            int value = 0, length = 0;
            while (length < 4 && p < input.Length && char.IsAsciiHexDigit(input[p]))
            {
                value = value * 16 + HexValue(input[p]);
                p++;
                length++;
            }

            if (At(input, p) == '.')
            {
                if (length == 0)
                    return false;
                p -= length;
                if (pieceIndex > 6)
                    return false;

                var numbersSeen = 0;
                while (p < input.Length)
                {
                    int? piece = null;
                    if (numbersSeen > 0)
                    {
                        if (input[p] == '.' && numbersSeen < 4)
                            p++;
                        else
                            return false;
                    }
                    if (!char.IsAsciiDigit(At(input, p)))
                        return false;
                    while (char.IsAsciiDigit(At(input, p)))
                    {
                        var number = input[p] - '0';
                        if (piece is null)
                            piece = number;
                        else if (piece == 0)
                            return false;
                        else
                            piece = piece * 10 + number;
                        if (piece > 255)
                            return false;
                        p++;
                    }
                    address[pieceIndex] = (ushort)(address[pieceIndex] * 0x100 + piece!.Value);
                    numbersSeen++;
                    if (numbersSeen is 2 or 4)
                        pieceIndex++;
                }
                if (numbersSeen != 4)
                    return false;
                break;
            }

            if (At(input, p) == ':')
            {
                p++;
                if (p >= input.Length)
                    return false;
            }
            else if (p < input.Length)
            {
                return false;
            }
            address[pieceIndex] = (ushort)value;
            pieceIndex++;
        }

        if (compress is { } c)
        {
            var swaps = pieceIndex - c;
            pieceIndex = 7;
            while (pieceIndex != 0 && swaps > 0)
            {
                (address[pieceIndex], address[c + swaps - 1]) = (address[c + swaps - 1], address[pieceIndex]);
                pieceIndex--;
                swaps--;
            }
        }
        else if (pieceIndex != 8)
        {
            return false;
        }
        return true;
    }

    private static char At(ReadOnlySpan<char> s, int index) => index < s.Length ? s[index] : '\0';

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static string SerializeIPv6(ushort[] address)
    {
        // Compress the first longest run of two or more zero pieces.
        int compress = -1, bestLength = 1;
        for (var i = 0; i < 8;)
        {
            if (address[i] != 0)
            {
                i++;
                continue;
            }
            var j = i;
            while (j < 8 && address[j] == 0)
                j++;
            if (j - i > bestLength)
            {
                compress = i;
                bestLength = j - i;
            }
            i = j;
        }

        var sb = new StringBuilder(39);
        var ignoreZero = false;
        for (var i = 0; i < 8; i++)
        {
            if (ignoreZero && address[i] == 0)
                continue;
            ignoreZero = false;
            if (i == compress)
            {
                sb.Append(i == 0 ? "::" : ":");
                ignoreZero = true;
                continue;
            }
            sb.Append(address[i].ToString("x", CultureInfo.InvariantCulture));
            if (i != 7)
                sb.Append(':');
        }
        return sb.ToString();
    }

    // ---- percent-encoding ----

    private static string PercentDecode(ReadOnlySpan<char> input)
    {
        if (!input.Contains('%'))
            return input.ToString();

        var bytes = new List<byte>(input.Length);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '%' && i + 2 < input.Length && char.IsAsciiHexDigit(input[i + 1]) && char.IsAsciiHexDigit(input[i + 2]))
            {
                bytes.Add((byte)(HexValue(input[i + 1]) * 16 + HexValue(input[i + 2])));
                i += 2;
                continue;
            }
            var length = char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]) ? 2 : 1;
            var written = Encoding.UTF8.GetBytes(input.Slice(i, length), utf8);
            for (var b = 0; b < written; b++)
                bytes.Add(utf8[b]);
            i += length - 1;
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Percent-encodes what the URL Standard encodes in paths (or special-scheme queries); keeps existing escapes.</summary>
    private static string PercentEncode(ReadOnlySpan<char> input, bool inQuery)
    {
        var needsWork = false;
        foreach (var c in input)
        {
            if (MustEncode(c, inQuery))
            {
                needsWork = true;
                break;
            }
        }
        if (!needsWork)
            return input.ToString();

        var sb = new StringBuilder(input.Length + 16);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (!MustEncode(c, inQuery))
            {
                sb.Append(c);
                continue;
            }
            var length = char.IsHighSurrogate(c) && i + 1 < input.Length && char.IsLowSurrogate(input[i + 1]) ? 2 : 1;
            var written = Encoding.UTF8.GetBytes(input.Slice(i, length), utf8);
            for (var b = 0; b < written; b++)
                sb.Append('%').Append(utf8[b].ToString("X2", CultureInfo.InvariantCulture));
            i += length - 1;
        }
        return sb.ToString();
    }

    private static bool MustEncode(char c, bool inQuery) =>
        c < 0x20 || c > 0x7E || c is ' ' or '"' or '<' or '>' || (inQuery ? c == '\'' : c is '`' or '{' or '}');

    private static bool IsSingleDotSegment(ReadOnlySpan<char> s) =>
        s is "." || s.Equals("%2e", StringComparison.OrdinalIgnoreCase);

    private static bool IsDoubleDotSegment(ReadOnlySpan<char> s) =>
        s is ".." || s.Equals(".%2e", StringComparison.OrdinalIgnoreCase)
        || s.Equals("%2e.", StringComparison.OrdinalIgnoreCase) || s.Equals("%2e%2e", StringComparison.OrdinalIgnoreCase);
}

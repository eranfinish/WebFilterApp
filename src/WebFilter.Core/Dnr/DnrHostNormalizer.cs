using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WebFilter.Core.Dnr;

internal enum DnrHostKind { Domain, IPv4, IPv6 }

/// <summary>
/// Normalizes a host written in a rule pattern to the form Chrome compares against <c>requestDomains</c>:
/// lowercase ASCII with IDN labels in punycode, no trailing dot, IPv4 in dotted decimal (WHATWG number forms resolved),
/// IPv6 compressed and in brackets. Kept inside Dnr/ on purpose: the compiler must not depend on Urls/.
/// </summary>
internal static class DnrHostNormalizer
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = true, UseStd3AsciiRules = false };

    public static bool TryNormalize(string input, out string host, out DnrHostKind kind)
    {
        host = "";
        kind = DnrHostKind.Domain;
        var s = input.Trim();
        if (s.Length == 0)
            return false;

        if (s[0] == '[')
            return s.Length > 2 && s[^1] == ']' && TryIPv6(s[1..^1], out host, out kind);
        if (s.Contains(':'))
            return TryIPv6(s, out host, out kind);

        if (s.Contains('%') && !TryPercentDecode(s, out s))
            return false;
        if (s.EndsWith('.'))
            s = s[..^1];
        if (s.Length == 0)
            return false;

        if (s.Any(c => c > 0x7F))
        {
            try
            {
                s = Idn.GetAscii(s.ToLowerInvariant());
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
        s = s.ToLowerInvariant();

        var labels = s.Split('.');
        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
                return false;
        }

        if (EndsInNumber(labels[^1]))
        {
            if (!TryIPv4(labels, out var ipv4))
                return false;
            host = ipv4;
            kind = DnrHostKind.IPv4;
            return true;
        }

        host = s;
        return true;
    }

    /// <summary>Label count used for specificity: domain labels, 4 for IPv4, 1 for IPv6.</summary>
    public static int Labels(string host, DnrHostKind kind) => kind switch
    {
        DnrHostKind.IPv6 => 1,
        _ => host.Count(c => c == '.') + 1,
    };

    // WHATWG "ends in a number": the last label is decimal digits or 0x-hex.
    private static bool EndsInNumber(string last) =>
        last.All(char.IsAsciiDigit) ||
        (last.Length >= 2 && last[0] == '0' && last[1] is 'x' or 'X' && last[2..].All(char.IsAsciiHexDigit));

    private static bool TryIPv4(string[] parts, out string result)
    {
        result = "";
        if (parts.Length > 4)
            return false;
        var numbers = new ulong[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryIPv4Number(parts[i], out numbers[i]))
                return false;
        }
        for (var i = 0; i < numbers.Length - 1; i++)
        {
            if (numbers[i] > 255)
                return false;
        }
        var last = numbers[^1];
        if (last >= 1UL << (8 * (5 - numbers.Length)))
            return false;
        var value = last;
        for (var i = 0; i < numbers.Length - 1; i++)
            value += numbers[i] << (8 * (3 - i));
        result = $"{value >> 24}.{(value >> 16) & 255}.{(value >> 8) & 255}.{value & 255}";
        return true;
    }

    private static bool TryIPv4Number(string part, out ulong value)
    {
        value = 0;
        if (part.Length == 0)
            return false;
        int radix = 10, start = 0;
        if (part.Length >= 2 && part[0] == '0' && part[1] is 'x' or 'X')
        {
            radix = 16;
            start = 2;
        }
        else if (part.Length >= 2 && part[0] == '0')
        {
            radix = 8;
            start = 1;
        }
        for (var i = start; i < part.Length; i++)
        {
            var digit = char.IsAsciiDigit(part[i]) ? part[i] - '0'
                : char.IsAsciiHexDigit(part[i]) ? char.ToLowerInvariant(part[i]) - 'a' + 10
                : -1;
            if (digit < 0 || digit >= radix)
                return false;
            value = value * (ulong)radix + (ulong)digit;
            if (value > uint.MaxValue)
                return false;
        }
        return true;
    }

    private static bool TryIPv6(string text, out string host, out DnrHostKind kind)
    {
        host = "";
        kind = DnrHostKind.IPv6;
        if (text.Contains('%') || !IPAddress.TryParse(text, out var address) || address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;
        host = "[" + SerializeIPv6(address.GetAddressBytes()) + "]";
        return true;
    }

    // WHATWG IPv6 serializer: lowercase hex, no leading zeros, the first longest run of 2+ zero pieces becomes "::".
    internal static string SerializeIPv6(byte[] bytes)
    {
        var pieces = new int[8];
        for (var i = 0; i < 8; i++)
            pieces[i] = (bytes[2 * i] << 8) | bytes[2 * i + 1];

        int bestStart = -1, bestLength = 1;
        for (var i = 0; i < 8;)
        {
            if (pieces[i] != 0)
            {
                i++;
                continue;
            }
            var j = i;
            while (j < 8 && pieces[j] == 0)
                j++;
            if (j - i > bestLength)
            {
                bestStart = i;
                bestLength = j - i;
            }
            i = j;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < 8; i++)
        {
            if (i == bestStart)
            {
                sb.Append(i == 0 ? "::" : ":");
                i += bestLength - 1;
                continue;
            }
            sb.Append(pieces[i].ToString("x", CultureInfo.InvariantCulture));
            if (i < 7)
                sb.Append(':');
        }
        return sb.ToString();
    }

    private static bool TryPercentDecode(string s, out string decoded)
    {
        decoded = s;
        var bytes = new List<byte>(s.Length);
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
            decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

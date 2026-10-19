namespace WebFilter.Core.Urls;

public enum HostKind { Domain, IPv4, IPv6 }

/// <summary>
/// A web URL after normalization (see tests/simulation/README.md §5 and normalization.json):
/// scheme is http/https; host is lowercase ASCII (IDN → punycode), percent-decoded, without a trailing dot;
/// IPv4 in dotted-decimal (decimal/hex/short forms resolved); IPv6 compressed lowercase without brackets;
/// default ports dropped; userinfo and fragment removed; dot-segments resolved in the path.
/// </summary>
public sealed record NormalizedUrl(string Scheme, string Host, HostKind HostKind, int? Port, string Path, string Query)
{
    public bool IsIpLiteral => HostKind != HostKind.Domain;

    /// <summary>Canonical string used for regex rules: <c>scheme://host[:port]path[?query]</c> (IPv6 hosts in brackets).</summary>
    public string Href
    {
        get
        {
            var host = HostKind == HostKind.IPv6 ? $"[{Host}]" : Host;
            var port = Port is null ? "" : $":{Port}";
            var query = Query.Length == 0 ? "" : $"?{Query}";
            return $"{Scheme}://{host}{port}{Path}{query}";
        }
    }

    public override string ToString() => Href;
}

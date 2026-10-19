using System.Diagnostics.CodeAnalysis;

namespace WebFilter.Core.Urls;

public static class UrlNormalizer
{
    /// <summary>True for http and https URLs. Other schemes (chrome:, edge:, about:, file:, data:) are not filtered by us.</summary>
    public static bool IsWebUrl(string input) => throw new NotImplementedException();

    /// <summary>Normalizes an http/https URL. Returns false for other schemes or unparseable input.</summary>
    public static bool TryNormalize(string input, [NotNullWhen(true)] out NormalizedUrl? url) => throw new NotImplementedException();

    /// <summary>
    /// Normalizes a host as written in a rule pattern ("Bücher.Example", "192.168.1.50", "[::1]") to the same form
    /// <see cref="TryNormalize"/> produces. Returns false if it is not a valid host.
    /// </summary>
    public static bool TryNormalizeHost(string host, [NotNullWhen(true)] out string? normalized, out HostKind kind) => throw new NotImplementedException();
}

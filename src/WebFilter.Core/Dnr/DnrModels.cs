using System.Text.Json.Serialization;

namespace WebFilter.Core.Dnr;

/// <summary>
/// chrome.declarativeNetRequest rule, serialized with the exact JSON shape the extension passes to
/// <c>updateDynamicRules</c> / <c>updateSessionRules</c>. Null members are omitted.
/// </summary>
public sealed record DnrRule(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("action")] DnrAction Action,
    [property: JsonPropertyName("condition")] DnrCondition Condition);

public sealed record DnrAction(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("redirect")] DnrRedirect? Redirect = null);

public sealed record DnrRedirect(
    [property: JsonPropertyName("regexSubstitution")] string? RegexSubstitution = null,
    [property: JsonPropertyName("extensionPath")] string? ExtensionPath = null);

public sealed record DnrCondition(
    [property: JsonPropertyName("resourceTypes")] IReadOnlyList<string> ResourceTypes,
    [property: JsonPropertyName("requestDomains")] IReadOnlyList<string>? RequestDomains = null,
    [property: JsonPropertyName("excludedRequestDomains")] IReadOnlyList<string>? ExcludedRequestDomains = null,
    [property: JsonPropertyName("regexFilter")] string? RegexFilter = null,
    [property: JsonPropertyName("urlFilter")] string? UrlFilter = null,
    [property: JsonPropertyName("isUrlFilterCaseSensitive")] bool? IsUrlFilterCaseSensitive = null);

/// <summary>What the extension installs for one member.</summary>
/// <param name="DynamicRules">The member's standing policy (installed with updateDynamicRules).</param>
/// <param name="TimeUpSessionRules">Installed with updateSessionRules while time is up: block everything except always-allowed.</param>
public sealed record DnrRuleSet(IReadOnlyList<DnrRule> DynamicRules, IReadOnlyList<DnrRule> TimeUpSessionRules);

/// <param name="ExtensionId">Used to build the block page URL for regexSubstitution redirects
/// (<c>chrome-extension://{id}/blocked.html#\0</c>, proven in spike S3).</param>
public sealed record DnrCompileOptions(string ExtensionId, string BlockPagePath = "/blocked.html");

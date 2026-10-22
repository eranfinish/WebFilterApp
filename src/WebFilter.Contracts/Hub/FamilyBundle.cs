using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebFilter.Contracts.Hub;

/// <summary>
/// The family settings as they travel from the hub to agents and as the service stores them (MVP-PLAN §10.3).
/// Platform-neutral: no Windows concepts (account ids are opaque strings), times are local "HH:MM" in
/// <see cref="TimeZone"/> (IANA id), instants are ISO-8601 with offset. The shape matches
/// tests/simulation/fixtures/*.json, so every fixture is a valid bundle.
/// </summary>
public sealed record FamilyBundle
{
    public int Version { get; init; }
    public required string TimeZone { get; init; }
    public IReadOnlyList<MemberDto> Members { get; init; } = [];
    public IReadOnlyList<DeviceDto> Devices { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Categories { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyList<MemberCategoryDto> MemberCategories { get; init; } = [];
    public IReadOnlyList<RuleDto> Rules { get; init; } = [];
    public IReadOnlyList<ScheduleDto> Schedules { get; init; } = [];
    public IReadOnlyList<DailyLimitDto> DailyLimits { get; init; } = [];
    public IReadOnlyList<YouTubeChannelDto> YouTubeChannels { get; init; } = [];
    public IReadOnlyList<string> SupportedBrowsers { get; init; } = [];
    public IReadOnlyList<AppRuleDto> AppRules { get; init; } = [];

    /// <summary>
    /// Family-wide Windows features for restricted and unlinked accounts (PROTECTION-SPEC §2). Null when the bundle has no
    /// such section (an older bundle): read as <see cref="WindowsFeaturesDto"/>'s defaults.
    /// </summary>
    public WindowsFeaturesDto? WindowsFeatures { get; init; }
}

/// <param name="Role">child | teen | adult.</param>
/// <param name="FilterMode">unrestricted | blocklist | allowlist.</param>
/// <param name="YouTubeMode">off | restricted | allowlist | unrestricted.</param>
/// <param name="BlockImageSearch">Image search pages are blocked (PROTECTION-SPEC §4). False when missing.</param>
/// <param name="JumpGuard">A site may not open another site in a new tab or window or by an automatic redirect (§7). False when missing.</param>
/// <param name="BlockYouTubeShorts">YouTube /shorts/ is blocked (§8). False when missing.</param>
/// <param name="LogSearches">Search terms are recorded for the parents; the member is told (§9). False when missing.</param>
/// <remarks>The four flags apply to restricted members only; unrestricted members (adults) ignore them.</remarks>
public sealed record MemberDto(string Id, string DisplayName, string Role, int? Age, string FilterMode, string YouTubeMode,
    bool BlockImageSearch = false, bool JumpGuard = false, bool BlockYouTubeShorts = false, bool LogSearches = false);

/// <summary>Wire: <c>windowsFeatures: { notificationsAndSync, oneDrive, webSearchInStart }</c>; a missing field takes its default.</summary>
/// <param name="NotificationsAndSync">Per-user Windows services (push notifications, settings sync) may use the network — svchost.exe is allowed, also when time is up.</param>
/// <param name="OneDrive">The machine-wide OneDrive may use the network, also when time is up. A per-user OneDrive never is.</param>
/// <param name="WebSearchInStart">Bing results in Start search; when false, DisableSearchBoxSuggestions is set for each such account.</param>
public sealed record WindowsFeaturesDto(bool NotificationsAndSync = true, bool OneDrive = true, bool WebSearchInStart = false);

public sealed record DeviceDto(string Id, string Os, string Role, IReadOnlyList<AccountDto> Accounts);

public sealed record AccountDto(string AccountId, string Name, string? Member, bool IsAdmin);

/// <param name="Action">allow | block.</param>
public sealed record MemberCategoryDto(string Member, string Category, string Action);

/// <param name="Type">domain | wildcard | prefix | regex.</param>
public sealed record RuleDto(string Id, string? Member, string Pattern, string Type, string Action, bool AlwaysAllowed = false, DateTimeOffset? ExpiresAt = null);

/// <param name="Days">0 = Sunday … 6 = Saturday.</param>
/// <param name="Start">"HH:MM".</param>
/// <param name="End">"HH:MM", "24:00" allowed; exclusive.</param>
public sealed record ScheduleDto(string Member, IReadOnlyList<int> Days, string Start, string End);

public sealed record DailyLimitDto(string Member, IReadOnlyList<int> Days, int Minutes);

public sealed record YouTubeChannelDto(string ChannelId, string? Handle, string? Title, string? Member, string Action);

/// <param name="Match">path | package | publisher.</param>
public sealed record AppRuleDto(string Id, string? Member, string Match, string Value, string Action, bool AlwaysAllowed = false, DateTimeOffset? ExpiresAt = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(FamilyBundle))]
public sealed partial class HubJsonContext : JsonSerializerContext;

public static class FamilyBundleJson
{
    public static string Serialize(FamilyBundle bundle) => JsonSerializer.Serialize(bundle, HubJsonContext.Default.FamilyBundle);

    public static FamilyBundle Deserialize(string json) =>
        JsonSerializer.Deserialize(json, HubJsonContext.Default.FamilyBundle) ?? throw new JsonException("Empty bundle");
}

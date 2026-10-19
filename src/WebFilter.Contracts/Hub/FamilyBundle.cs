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
}

/// <param name="Role">child | teen | adult.</param>
/// <param name="FilterMode">unrestricted | blocklist | allowlist.</param>
/// <param name="YouTubeMode">off | restricted | allowlist | unrestricted.</param>
public sealed record MemberDto(string Id, string DisplayName, string Role, int? Age, string FilterMode, string YouTubeMode);

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

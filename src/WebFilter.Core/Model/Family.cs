namespace WebFilter.Core.Model;

public enum Role { Child, Teen, Adult }

public enum FilterMode { Unrestricted, Blocklist, Allowlist }

public enum YouTubeMode { Off, Restricted, Allowlist, Unrestricted }

public enum RuleAction { Allow, Block }

public enum PatternType { Domain, Wildcard, Prefix, Regex }

public enum AppMatchType { Path, Package, Publisher }

/// <param name="BlockImageSearch">Image search pages are blocked (PROTECTION-SPEC §4).</param>
/// <param name="JumpGuard">A site may not open another site in a new tab or window or by an automatic redirect (extension, §7).</param>
/// <param name="BlockYouTubeShorts">YouTube /shorts/ is blocked (extension, §8).</param>
/// <param name="LogSearches">Search terms are recorded for the parents, and the member is told so (§9).</param>
/// <param name="BlockAds">Requests to EasyList's ad servers are blocked (extension, §12).</param>
/// <remarks>The five protection flags apply only to restricted members; unrestricted members ignore them (§1).</remarks>
public sealed record FamilyMember(string Id, string DisplayName, Role Role, int? Age, FilterMode FilterMode, YouTubeMode YouTubeMode,
    bool BlockImageSearch = false, bool JumpGuard = false, bool BlockYouTubeShorts = false, bool LogSearches = false, bool BlockAds = false)
{
    public bool IsRestricted => FilterMode != FilterMode.Unrestricted;
}

/// <summary>
/// What restricted and unlinked accounts may use of Windows itself (PROTECTION-SPEC §2), family-wide. The defaults are
/// what a bundle without the section means.
/// </summary>
/// <param name="NotificationsAndSync">Per-user Windows services (push notifications, settings sync) reach the network: svchost.exe is allowed, also when time is up.</param>
/// <param name="OneDrive">The machine-wide OneDrive reaches the network, also when time is up. Never a per-user install.</param>
/// <param name="WebSearchInStart">Bing results in Start search. When false, the per-user policy DisableSearchBoxSuggestions is set.</param>
public sealed record WindowsFeatures(bool NotificationsAndSync = true, bool OneDrive = true, bool WebSearchInStart = false)
{
    public static readonly WindowsFeatures Default = new();
}

/// <summary>A Windows (or Android) account on a device. <see cref="MemberId"/> is null until a parent links it.</summary>
/// <param name="Guest">
/// The parents confirmed that this account stays unlinked (a guest). Decisions do not read it: a guest is restricted
/// like every unlinked account. It only records the parents' answer, so the admin app stops asking; a report of the
/// device's accounts keeps it, and linking the account drops it.
/// </param>
public sealed record DeviceAccount(string AccountId, string Name, string? MemberId, bool IsAdmin, bool Guest = false);

public sealed record Device(string Id, string Os, string Role, IReadOnlyList<DeviceAccount> Accounts);

/// <summary>A URL rule. <see cref="MemberId"/> null = global. <see cref="ExpiresAt"/> is exclusive.</summary>
public sealed record Rule(
    string Id,
    string? MemberId,
    string Pattern,
    PatternType Type,
    RuleAction Action,
    bool AlwaysAllowed = false,
    DateTimeOffset? ExpiresAt = null)
{
    public bool IsActiveAt(DateTimeOffset now) => ExpiresAt is null || now < ExpiresAt;
}

public sealed record MemberCategory(string MemberId, string Category, RuleAction Action);

/// <summary>A time window <c>[StartMinute, EndMinute)</c> in local minutes of day (0–1440) on the given days.</summary>
public sealed record Schedule(string MemberId, IReadOnlySet<DayOfWeek> Days, int StartMinute, int EndMinute);

public sealed record DailyLimit(string MemberId, IReadOnlySet<DayOfWeek> Days, int Minutes);

public sealed record YouTubeChannel(string ChannelId, string? Handle, string? Title, string? MemberId, RuleAction Action);

public sealed record AppRule(
    string Id,
    string? MemberId,
    AppMatchType Match,
    string Value,
    RuleAction Action,
    bool AlwaysAllowed = false,
    DateTimeOffset? ExpiresAt = null)
{
    public bool IsActiveAt(DateTimeOffset now) => ExpiresAt is null || now < ExpiresAt;
}

/// <summary>The family's settings as owned by the hub. Mutable: admin actions edit it in place.</summary>
public sealed class FamilyConfig
{
    public required TimeZoneInfo TimeZone { get; init; }
    public List<FamilyMember> Members { get; init; } = [];
    public List<Device> Devices { get; init; } = [];
    /// <summary>Category name → registrable domains.</summary>
    public Dictionary<string, IReadOnlyList<string>> Categories { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MemberCategory> MemberCategories { get; init; } = [];
    public List<Rule> Rules { get; init; } = [];
    public List<Schedule> Schedules { get; init; } = [];
    public List<DailyLimit> DailyLimits { get; init; } = [];
    public List<YouTubeChannel> YouTubeChannels { get; init; } = [];
    public List<string> SupportedBrowsers { get; init; } = [];
    public List<AppRule> AppRules { get; init; } = [];
    public WindowsFeatures WindowsFeatures { get; init; } = WindowsFeatures.Default;

    public const string EducationalCategory = "educational";

    public FamilyMember Member(string id) =>
        Members.Find(m => m.Id == id) ?? throw new KeyNotFoundException($"Unknown member '{id}'");

    public Device Device(string id) =>
        Devices.Find(d => d.Id == id) ?? throw new KeyNotFoundException($"Unknown device '{id}'");
}

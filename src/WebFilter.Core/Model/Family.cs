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
/// <param name="YouTubeChannelSearch">
/// In YouTube's "approved channels only" mode the member may search YouTube for channels: YouTube's results restricted to
/// channels are allowed, and any other search is sent to them (§15). A permission rather than a protection; it changes
/// nothing in the other YouTube modes.
/// </param>
/// <remarks>The flags apply only to restricted members; unrestricted members ignore them (§1).</remarks>
public sealed record FamilyMember(string Id, string DisplayName, Role Role, int? Age, FilterMode FilterMode, YouTubeMode YouTubeMode,
    bool BlockImageSearch = false, bool JumpGuard = false, bool BlockYouTubeShorts = false, bool LogSearches = false, bool BlockAds = false,
    bool YouTubeChannelSearch = false)
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

/// <summary>
/// The family's baseline protection for the parents' own browsing (PROTECTION-SPEC §13), family-wide: what administrator
/// accounts and unrestricted members' accounts get (<see cref="AppliesTo"/>). The defaults are what a bundle without the
/// section means: all on, so existing families get it. Children, teens and unlinked standard accounts follow their own
/// settings, which are already stronger.
/// </summary>
/// <param name="BlockAdult">The family's <see cref="AdultCategory"/> category is blocked (<c>blocked_baseline_adult</c>), unless a parent's allow rule matches. A family without the category blocks nothing.</param>
/// <param name="JumpGuard">The extension's jump guard (§7), with "open anyway".</param>
/// <param name="BlockAds">The extension's ad-server ruleset (§12).</param>
/// <param name="SafeSearch">Per-user browser policies force Google SafeSearch (Chrome and Edge) and Bing SafeSearch, moderate (Edge).</param>
public sealed record BaselineProtection(bool BlockAdult = true, bool JumpGuard = true, bool BlockAds = true, bool SafeSearch = true)
{
    public static readonly BaselineProtection Default = new();

    /// <summary>The starter-list category of adult sites (data/starter-lists/adult.json), which the admin app copies into the bundle.</summary>
    public const string AdultCategory = "adult";

    /// <summary>Any of the four is on: the extension is told it is in the parents' baseline mode, not unrestricted.</summary>
    public bool IsOn => BlockAdult || JumpGuard || BlockAds || SafeSearch;

    /// <summary>
    /// Whether <paramref name="account"/> is one of the parents' (or another adult's): an administrator account linked to
    /// nobody, or an account linked to an unrestricted member. These are the accounts no member settings filter; the
    /// baseline is all they get, and nothing of their browsing is reported. An administrator account linked to a filtered
    /// member is filtered as that member.
    /// </summary>
    public static bool AppliesTo(FamilyConfig config, DeviceAccount account)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(account);
        if (account.MemberId is null) return account.IsAdmin;
        return config.Members.Find(m => m.Id == account.MemberId) is { IsRestricted: false };
    }
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
    public BaselineProtection Baseline { get; init; } = BaselineProtection.Default;

    public const string EducationalCategory = "educational";

    public FamilyMember Member(string id) =>
        Members.Find(m => m.Id == id) ?? throw new KeyNotFoundException($"Unknown member '{id}'");

    public Device Device(string id) =>
        Devices.Find(d => d.Id == id) ?? throw new KeyNotFoundException($"Unknown device '{id}'");
}

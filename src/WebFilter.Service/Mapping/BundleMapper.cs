using System.Globalization;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Model;

namespace WebFilter.Service.Mapping;

/// <summary>Core settings ⇄ the platform-neutral <see cref="FamilyBundle"/>.</summary>
public static class BundleMapper
{
    public static FamilyBundle ToBundle(FamilyConfig config, int version) => new()
    {
        Version = version,
        TimeZone = ZoneId(config.TimeZone),
        Members = config.Members.Select(m => new MemberDto(m.Id, m.DisplayName, Lower(m.Role), m.Age, Lower(m.FilterMode), Lower(m.YouTubeMode),
            m.BlockImageSearch, m.JumpGuard, m.BlockYouTubeShorts, m.LogSearches, m.BlockAds, m.YouTubeChannelSearch)).ToList(),
        Devices = config.Devices.Select(d => new DeviceDto(d.Id, d.Os, d.Role, d.Accounts.Select(a => new AccountDto(a.AccountId, a.Name, a.MemberId, a.IsAdmin, a.Guest)).ToList())).ToList(),
        Categories = config.Categories.ToDictionary(kv => kv.Key, kv => kv.Value),
        MemberCategories = config.MemberCategories.Select(c => new MemberCategoryDto(c.MemberId, c.Category, Lower(c.Action))).ToList(),
        Rules = config.Rules.Select(r => new RuleDto(r.Id, r.MemberId, r.Pattern, Lower(r.Type), Lower(r.Action), r.AlwaysAllowed, r.ExpiresAt)).ToList(),
        Schedules = config.Schedules.Select(s => new ScheduleDto(s.MemberId, Days(s.Days), HhMm(s.StartMinute), HhMm(s.EndMinute))).ToList(),
        DailyLimits = config.DailyLimits.Select(l => new DailyLimitDto(l.MemberId, Days(l.Days), l.Minutes)).ToList(),
        YouTubeChannels = config.YouTubeChannels.Select(c => new YouTubeChannelDto(c.ChannelId, c.Handle, c.Title, c.MemberId, Lower(c.Action))).ToList(),
        SupportedBrowsers = [.. config.SupportedBrowsers],
        AppRules = config.AppRules.Select(r => new AppRuleDto(r.Id, r.MemberId, Lower(r.Match), r.Value, Lower(r.Action), r.AlwaysAllowed, r.ExpiresAt)).ToList(),
        WindowsFeatures = new WindowsFeaturesDto(config.WindowsFeatures.NotificationsAndSync, config.WindowsFeatures.OneDrive, config.WindowsFeatures.WebSearchInStart),
        Baseline = new BaselineDto(config.Baseline.BlockAdult, config.Baseline.JumpGuard, config.Baseline.BlockAds, config.Baseline.SafeSearch),
    };

    /// <remarks>
    /// A list the JSON leaves out arrives as null (the property initializers do not run when deserializing), so every
    /// list is read as empty when missing — a bootstrap file, snapshot or admin request with fewer sections is valid.
    /// Missing member flags are false and missing Windows features and baseline switches take their defaults
    /// (PROTECTION-SPEC §1, §2, §13: the baseline is on when missing). An
    /// account is a guest only while it is unlinked: linking it drops the flag, whoever sent the bundle.
    /// </remarks>
    public static FamilyConfig ToConfig(FamilyBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var config = new FamilyConfig
        {
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById(bundle.TimeZone),
            Members = (bundle.Members ?? []).Select(m => new FamilyMember(m.Id, m.DisplayName, Parse<Role>(m.Role), m.Age, Parse<FilterMode>(m.FilterMode), Parse<YouTubeMode>(m.YouTubeMode),
                m.BlockImageSearch, m.JumpGuard, m.BlockYouTubeShorts, m.LogSearches, m.BlockAds, m.YouTubeChannelSearch)).ToList(),
            Devices = (bundle.Devices ?? []).Select(d => new Device(d.Id, d.Os, d.Role, (d.Accounts ?? [])
                .Select(a => new DeviceAccount(a.AccountId, a.Name, a.Member, a.IsAdmin, a.Guest && a.Member is null)).ToList())).ToList(),
            MemberCategories = (bundle.MemberCategories ?? []).Select(c => new MemberCategory(c.Member, c.Category, Parse<RuleAction>(c.Action))).ToList(),
            Rules = (bundle.Rules ?? []).Select(r => new Rule(r.Id, r.Member, r.Pattern, Parse<PatternType>(r.Type), Parse<RuleAction>(r.Action), r.AlwaysAllowed, r.ExpiresAt)).ToList(),
            Schedules = (bundle.Schedules ?? []).Select(s => new Schedule(s.Member, DaySet(s.Days ?? []), Minutes(s.Start), Minutes(s.End))).ToList(),
            DailyLimits = (bundle.DailyLimits ?? []).Select(l => new DailyLimit(l.Member, DaySet(l.Days ?? []), l.Minutes)).ToList(),
            YouTubeChannels = (bundle.YouTubeChannels ?? []).Select(c => new YouTubeChannel(c.ChannelId, c.Handle, c.Title, c.Member, Parse<RuleAction>(c.Action))).ToList(),
            SupportedBrowsers = [.. bundle.SupportedBrowsers ?? []],
            AppRules = (bundle.AppRules ?? []).Select(r => new AppRule(r.Id, r.Member, Parse<AppMatchType>(r.Match), r.Value, Parse<RuleAction>(r.Action), r.AlwaysAllowed, r.ExpiresAt)).ToList(),
            WindowsFeatures = bundle.WindowsFeatures is { } features
                ? new WindowsFeatures(features.NotificationsAndSync, features.OneDrive, features.WebSearchInStart)
                : WindowsFeatures.Default,
            Baseline = bundle.Baseline is { } baseline
                ? new BaselineProtection(baseline.BlockAdult, baseline.JumpGuard, baseline.BlockAds, baseline.SafeSearch)
                : BaselineProtection.Default,
        };
        foreach (var (name, domains) in bundle.Categories ?? new Dictionary<string, IReadOnlyList<string>>()) config.Categories[name] = domains ?? [];
        return config;
    }

    /// <summary>The zone's IANA id (platform-neutral), or its own id when it has none.</summary>
    public static string ZoneId(TimeZoneInfo zone) => TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static T Parse<T>(string value) where T : struct, Enum => Enum.Parse<T>(value, ignoreCase: true);

    private static List<int> Days(IReadOnlySet<DayOfWeek> days) => days.Select(d => (int)d).Order().ToList();

    private static HashSet<DayOfWeek> DaySet(IReadOnlyList<int> days) => days.Select(d => (DayOfWeek)d).ToHashSet();

    private static string HhMm(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";

    private static int Minutes(string hhmm) =>
        int.Parse(hhmm[..2], CultureInfo.InvariantCulture) * 60 + int.Parse(hhmm[3..5], CultureInfo.InvariantCulture);
}

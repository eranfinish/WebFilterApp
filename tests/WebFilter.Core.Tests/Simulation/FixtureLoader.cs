using System.Globalization;
using System.Text.Json.Nodes;
using WebFilter.Core.Model;

namespace WebFilter.Core.Tests.Simulation;

/// <summary>Builds a <see cref="FamilyConfig"/> from a fixture plus a scenario's setup patch (README §3).</summary>
internal static class FixtureLoader
{
    public static FamilyConfig Load(JsonObject fixture, JsonObject? setup)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(Str(fixture, "timezone"));
        var config = new FamilyConfig
        {
            TimeZone = zone,
            Members = Array(fixture, "members").Select(ParseMember).ToList(),
            Devices = Array(fixture, "devices").Select(ParseDevice).ToList(),
            MemberCategories = Array(fixture, "memberCategories").Select(ParseMemberCategory).ToList(),
            Rules = Array(fixture, "rules").Select(r => ParseRule(r, zone)).ToList(),
            Schedules = Array(fixture, "schedules").Select(ParseSchedule).ToList(),
            DailyLimits = Array(fixture, "dailyLimits").Select(ParseLimit).ToList(),
            YouTubeChannels = Array(fixture, "youtubeChannels").Select(ParseChannel).ToList(),
            SupportedBrowsers = Array(fixture, "supportedBrowsers").Select(n => n!.GetValue<string>()).ToList(),
            AppRules = Array(fixture, "appRules").Select(ParseAppRule).ToList(),
            Baseline = ParseBaseline(setup?["baseline"]),
        };
        foreach (var (name, domains) in fixture["categories"]!.AsObject())
            config.Categories[name] = domains!.AsArray().Select(d => d!.GetValue<string>()).ToList();

        if (setup is not null) ApplySetup(config, setup);
        return config;
    }

    private static void ApplySetup(FamilyConfig config, JsonObject setup)
    {
        foreach (var patch in Array(setup, "members"))
        {
            var index = config.Members.FindIndex(m => m.Id == Str(patch, "id"));
            config.Members[index] = PatchMember(config.Members[index], patch!.AsObject());
        }
        var scheduled = Array(setup, "schedules").Select(ParseSchedule).ToList();
        foreach (var memberId in scheduled.Select(s => s.MemberId).Distinct())
            config.Schedules.RemoveAll(s => s.MemberId == memberId);
        config.Schedules.AddRange(scheduled);

        foreach (var limit in Array(setup, "dailyLimits").Select(ParseLimit))
        {
            // Replace this member's limit on exactly these days, keeping the fixture's other days.
            var affected = config.DailyLimits.Where(l => l.MemberId == limit.MemberId && l.Days.Overlaps(limit.Days)).ToList();
            foreach (var old in affected)
            {
                config.DailyLimits.Remove(old);
                var rest = old.Days.Except(limit.Days).ToHashSet();
                if (rest.Count > 0) config.DailyLimits.Add(old with { Days = rest });
            }
            config.DailyLimits.Add(limit);
        }
        foreach (var (name, domains) in setup["categories"]?.AsObject() ?? [])
            config.Categories[name] = domains!.AsArray().Select(d => d!.GetValue<string>()).ToList();
        config.Rules.AddRange(Array(setup, "rules").Select(r => ParseRule(r, config.TimeZone)));
        config.MemberCategories.AddRange(Array(setup, "memberCategories").Select(ParseMemberCategory));
        config.YouTubeChannels.AddRange(Array(setup, "youtubeChannels").Select(ParseChannel));
        config.AppRules.AddRange(Array(setup, "appRules").Select(ParseAppRule));
    }

    public static FamilyMember PatchMember(FamilyMember member, JsonObject patch) => member with
    {
        DisplayName = OptStr(patch, "displayName") ?? member.DisplayName,
        Role = OptStr(patch, "role") is { } role ? ParseRole(role) : member.Role,
        Age = patch["age"]?.GetValue<int>() ?? member.Age,
        FilterMode = OptStr(patch, "filterMode") is { } fm ? ParseFilterMode(fm) : member.FilterMode,
        YouTubeMode = OptStr(patch, "youtubeMode") is { } ym ? ParseYouTubeMode(ym) : member.YouTubeMode,
        BlockImageSearch = patch["blockImageSearch"]?.GetValue<bool>() ?? member.BlockImageSearch,
        JumpGuard = patch["jumpGuard"]?.GetValue<bool>() ?? member.JumpGuard,
        BlockYouTubeShorts = patch["blockYouTubeShorts"]?.GetValue<bool>() ?? member.BlockYouTubeShorts,
        LogSearches = patch["logSearches"]?.GetValue<bool>() ?? member.LogSearches,
        BlockAds = patch["blockAds"]?.GetValue<bool>() ?? member.BlockAds,
    };

    private static FamilyMember ParseMember(JsonNode? n) => PatchMember(new(
        Str(n, "id"), OptStr(n, "displayName") ?? Str(n, "id"), ParseRole(Str(n, "role")), n!["age"]?.GetValue<int>(),
        ParseFilterMode(Str(n, "filterMode")), ParseYouTubeMode(Str(n, "youtubeMode"))), n.AsObject());

    private static Device ParseDevice(JsonNode? n) => new(
        Str(n, "id"), Str(n, "os"), Str(n, "role"),
        Array(n, "accounts").Select(a => new DeviceAccount(Str(a, "accountId"), Str(a, "name"), OptStr(a, "member"), a!["isAdmin"]!.GetValue<bool>(),
            a["guest"]?.GetValue<bool>() ?? false)).ToList());

    public static Rule ParseRule(JsonNode? n, TimeZoneInfo zone) => new(
        Str(n, "id"), OptStr(n, "member"), Str(n, "pattern"), ParsePatternType(Str(n, "type")),
        ParseAction(Str(n, "action")), n!["alwaysAllowed"]?.GetValue<bool>() ?? false,
        OptStr(n, "expiresAt") is { } expires ? LocalTime.ToInstant(DateTime.Parse(expires, CultureInfo.InvariantCulture), zone) : null);

    private static MemberCategory ParseMemberCategory(JsonNode? n) => new(Str(n, "member"), Str(n, "category"), ParseAction(Str(n, "action")));

    /// <summary>The setup's baseline switches (PROTECTION-SPEC §13); a missing section or switch is on.</summary>
    public static BaselineProtection ParseBaseline(JsonNode? n) => n is null ? BaselineProtection.Default : new(
        n["blockAdult"]?.GetValue<bool>() ?? true, n["jumpGuard"]?.GetValue<bool>() ?? true,
        n["blockAds"]?.GetValue<bool>() ?? true, n["safeSearch"]?.GetValue<bool>() ?? true);

    private static Schedule ParseSchedule(JsonNode? n) => new(Str(n, "member"), Days(n), Minutes(Str(n, "start")), Minutes(Str(n, "end")));

    private static DailyLimit ParseLimit(JsonNode? n) => new(Str(n, "member"), Days(n), n!["minutes"]!.GetValue<int>());

    private static YouTubeChannel ParseChannel(JsonNode? n) =>
        new(Str(n, "channelId"), OptStr(n, "handle"), OptStr(n, "title"), OptStr(n, "member"), ParseAction(Str(n, "action")));

    private static AppRule ParseAppRule(JsonNode? n) => new(
        Str(n, "id"), OptStr(n, "member"),
        Str(n, "match") switch { "path" => AppMatchType.Path, "package" => AppMatchType.Package, _ => AppMatchType.Publisher },
        Str(n, "value"), ParseAction(Str(n, "action")), n!["alwaysAllowed"]?.GetValue<bool>() ?? false);

    private static HashSet<DayOfWeek> Days(JsonNode? n) => Array(n, "days").Select(d => (DayOfWeek)d!.GetValue<int>()).ToHashSet();

    private static int Minutes(string hhmm) => hhmm == "24:00" ? 1440 : int.Parse(hhmm[..2], CultureInfo.InvariantCulture) * 60 + int.Parse(hhmm[3..5], CultureInfo.InvariantCulture);

    public static Role ParseRole(string s) => s switch { "child" => Role.Child, "teen" => Role.Teen, _ => Role.Adult };
    public static FilterMode ParseFilterMode(string s) => s switch { "blocklist" => FilterMode.Blocklist, "allowlist" => FilterMode.Allowlist, _ => FilterMode.Unrestricted };
    public static YouTubeMode ParseYouTubeMode(string s) => s switch { "off" => YouTubeMode.Off, "restricted" => YouTubeMode.Restricted, "allowlist" => YouTubeMode.Allowlist, _ => YouTubeMode.Unrestricted };
    public static PatternType ParsePatternType(string s) => s switch { "domain" => PatternType.Domain, "wildcard" => PatternType.Wildcard, "prefix" => PatternType.Prefix, _ => PatternType.Regex };
    public static RuleAction ParseAction(string s) => s == "allow" ? RuleAction.Allow : RuleAction.Block;

    internal static IEnumerable<JsonNode?> Array(JsonNode? n, string key) => n?[key]?.AsArray() ?? [];
    internal static string Str(JsonNode? n, string key) => n![key]!.GetValue<string>();
    internal static string? OptStr(JsonNode? n, string key) => n?[key]?.GetValue<string>();
}

/// <summary>Scenario times are local wall-clock in the family time zone.</summary>
internal static class LocalTime
{
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone) =>
        new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified)));

    /// <summary>"HH:MM[:SS]" on the current local date, or "YYYY-MM-DDTHH:MM[:SS]".</summary>
    public static DateTimeOffset Parse(string value, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (value.Contains('T')) return ToInstant(DateTime.Parse(value, CultureInfo.InvariantCulture), zone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var time = TimeSpan.Parse(value.Length == 5 ? value + ":00" : value, CultureInfo.InvariantCulture);
        return ToInstant(localNow.Date + time, zone);
    }

    public static string HhMm(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string Full(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
}

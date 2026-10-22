using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using WebFilter.Core.Model;

namespace WebFilter.Core.Tests.Urls;

/// <summary>
/// Reads the simulation scenario files and fixtures for the URL and YouTube tests. Only what URL rules need is
/// modeled (members, categories, member categories, rules, and admin rule add/remove); the full level-1 runner lives elsewhere.
/// </summary>
internal static class ScenarioData
{
    private static readonly ConcurrentDictionary<string, JsonElement> Files = new();

    public static string RepoRoot { get; } = FindRepoRoot();

    public static IEnumerable<JsonElement> Scenarios(string suiteFile) =>
        Suite(suiteFile).GetProperty("scenarios").EnumerateArray();

    public static JsonElement Scenario(string suiteFile, string id) =>
        Scenarios(suiteFile).Single(s => s.GetProperty("id").GetString() == id);

    /// <summary>Enum from a scenario wire value: "not_applicable" → NotApplicable, "nocookie" → NoCookie.</summary>
    public static T ParseEnum<T>(string wire) where T : struct, Enum => Enum.Parse<T>(wire.Replace("_", ""), ignoreCase: true);

    public static string? OptString(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The fixture plus the scenario's <c>setup</c> (members merged by id; rules and member categories appended).</summary>
    public static FamilyConfig BuildConfig(string suiteFile, JsonElement scenario)
    {
        var fixtureName = OptString(scenario, "fixture") ?? OptString(Suite(suiteFile), "fixture") ?? "family-base";
        var fixture = Load(Path.Combine(RepoRoot, "tests", "simulation", "fixtures", fixtureName + ".json"));
        var timeZone = FindTimeZone(fixture.GetProperty("timezone").GetString()!);

        var config = new FamilyConfig { TimeZone = timeZone };
        foreach (var m in fixture.GetProperty("members").EnumerateArray())
            config.Members.Add(ParseMember(m, null));
        foreach (var category in fixture.GetProperty("categories").EnumerateObject())
            config.Categories[category.Name] = category.Value.EnumerateArray().Select(d => d.GetString()!).ToList();
        AddMemberCategories(config, fixture);
        AddRules(config, fixture, timeZone);

        if (scenario.TryGetProperty("setup", out var setup))
        {
            if (setup.TryGetProperty("members", out var members))
            {
                foreach (var patch in members.EnumerateArray())
                {
                    var index = config.Members.FindIndex(x => x.Id == patch.GetProperty("id").GetString());
                    config.Members[index] = ParseMember(patch, config.Members[index]);
                }
            }
            if (setup.TryGetProperty("categories", out var categories))
                foreach (var category in categories.EnumerateObject())
                    config.Categories[category.Name] = category.Value.EnumerateArray().Select(d => d.GetString()!).ToList();
            AddMemberCategories(config, setup);
            AddRules(config, setup, timeZone);
        }
        return config;
    }

    public static Rule ParseRule(JsonElement e, TimeZoneInfo timeZone) => new(
        e.GetProperty("id").GetString()!,
        OptString(e, "member"),
        e.GetProperty("pattern").GetString()!,
        ParseEnum<PatternType>(e.GetProperty("type").GetString()!),
        ParseEnum<RuleAction>(e.GetProperty("action").GetString()!),
        e.TryGetProperty("alwaysAllowed", out var always) && always.GetBoolean(),
        OptString(e, "expiresAt") is { } expires ? ParseLocal(expires, timeZone) : null);

    /// <summary>A full local timestamp ("2026-11-01T17:00") in the fixture's time zone.</summary>
    public static DateTimeOffset ParseLocal(string text, TimeZoneInfo timeZone)
    {
        var local = DateTime.ParseExact(text, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }

    /// <summary>A step's <c>at</c>: "HH:MM" on the current simulated day, or a full local timestamp.</summary>
    public static DateTimeOffset ParseAt(string text, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        if (text.Contains('T'))
            return ParseLocal(text, timeZone);
        var today = TimeZoneInfo.ConvertTime(now, timeZone).DateTime.Date;
        var local = today + TimeSpan.ParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture);
        return new DateTimeOffset(local, timeZone.GetUtcOffset(local));
    }

    private static JsonElement Suite(string suiteFile) =>
        Load(Path.Combine(RepoRoot, "tests", "simulation", "scenarios", "engine", suiteFile));

    private static JsonElement Load(string path) =>
        Files.GetOrAdd(path, p =>
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            return doc.RootElement.Clone();
        });

    private static FamilyMember ParseMember(JsonElement e, FamilyMember? baseline) => new(
        e.GetProperty("id").GetString()!,
        OptString(e, "displayName") ?? baseline?.DisplayName ?? "",
        OptString(e, "role") is { } role ? ParseEnum<Role>(role) : baseline?.Role ?? Role.Child,
        e.TryGetProperty("age", out var age) ? age.GetInt32() : baseline?.Age,
        OptString(e, "filterMode") is { } filter ? ParseEnum<FilterMode>(filter) : baseline?.FilterMode ?? FilterMode.Allowlist,
        OptString(e, "youtubeMode") is { } youtube ? ParseEnum<YouTubeMode>(youtube) : baseline?.YouTubeMode ?? YouTubeMode.Off,
        e.TryGetProperty("blockImageSearch", out var images) ? images.GetBoolean() : baseline?.BlockImageSearch ?? false,
        e.TryGetProperty("jumpGuard", out var jump) ? jump.GetBoolean() : baseline?.JumpGuard ?? false,
        e.TryGetProperty("blockYouTubeShorts", out var shorts) ? shorts.GetBoolean() : baseline?.BlockYouTubeShorts ?? false,
        e.TryGetProperty("logSearches", out var searches) ? searches.GetBoolean() : baseline?.LogSearches ?? false);

    private static void AddMemberCategories(FamilyConfig config, JsonElement source)
    {
        if (!source.TryGetProperty("memberCategories", out var list))
            return;
        foreach (var mc in list.EnumerateArray())
        {
            config.MemberCategories.Add(new MemberCategory(
                mc.GetProperty("member").GetString()!,
                mc.GetProperty("category").GetString()!,
                ParseEnum<RuleAction>(mc.GetProperty("action").GetString()!)));
        }
    }

    private static void AddRules(FamilyConfig config, JsonElement source, TimeZoneInfo timeZone)
    {
        if (!source.TryGetProperty("rules", out var list))
            return;
        foreach (var rule in list.EnumerateArray())
            config.Rules.Add(ParseRule(rule, timeZone));
    }

    private static TimeZoneInfo FindTimeZone(string ianaId)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(ianaId, out var zone))
            return zone;
        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId, out var windowsId) && TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out zone))
            return zone;
        throw new InvalidOperationException($"Time zone '{ianaId}' is not available on this machine.");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("WebFilter.slnx not found above " + AppContext.BaseDirectory);
    }
}

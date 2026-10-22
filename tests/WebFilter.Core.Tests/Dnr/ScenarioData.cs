using System.Globalization;
using System.Text.Json;
using WebFilter.Core.Model;

namespace WebFilter.Core.Tests.Dnr;

/// <summary>Builds <see cref="FamilyConfig"/> from tests/simulation (fixture + a scenario's <c>setup</c>, README §3) — URL-policy parts only.</summary>
internal static class ScenarioData
{
    public static readonly string RepoRoot = FindRepoRoot();
    public static readonly string ScenariosDir = Path.Combine(RepoRoot, "tests", "simulation", "scenarios", "engine");

    private static readonly Lazy<JsonElement> Fixture = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "tests", "simulation", "fixtures", "family-base.json"))).RootElement);

    public static IEnumerable<JsonElement> Scenarios(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ScenariosDir, file)));
        return doc.RootElement.GetProperty("scenarios").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public static FamilyConfig BuildConfig(JsonElement? setup = null)
    {
        var fixture = Fixture.Value;
        var config = new FamilyConfig
        {
            TimeZone = TimeZoneInfo.FindSystemTimeZoneById(fixture.GetProperty("timezone").GetString()!),
        };

        var members = fixture.GetProperty("members").EnumerateArray().Select(m => m.Clone()).ToList();
        foreach (var m in members)
        {
            var id = m.GetProperty("id").GetString()!;
            var patch = setup is { } s && s.TryGetProperty("members", out var sm)
                ? sm.EnumerateArray().FirstOrDefault(p => p.GetProperty("id").GetString() == id)
                : default;
            string Field(string name) =>
                patch.ValueKind == JsonValueKind.Object && patch.TryGetProperty(name, out var v) ? v.GetString()! : m.GetProperty(name).GetString()!;
            bool Flag(string name) =>
                patch.ValueKind == JsonValueKind.Object && patch.TryGetProperty(name, out var v) ? v.GetBoolean()
                : m.TryGetProperty(name, out var f) && f.GetBoolean();
            config.Members.Add(new FamilyMember(
                id,
                m.GetProperty("displayName").GetString()!,
                Enum.Parse<Role>(Field("role"), ignoreCase: true),
                m.TryGetProperty("age", out var age) ? age.GetInt32() : null,
                Enum.Parse<FilterMode>(Field("filterMode"), ignoreCase: true),
                Enum.Parse<YouTubeMode>(Field("youtubeMode"), ignoreCase: true),
                Flag("blockImageSearch"), Flag("jumpGuard"), Flag("blockYouTubeShorts"), Flag("logSearches"), Flag("blockAds")));
        }

        foreach (var category in fixture.GetProperty("categories").EnumerateObject())
            config.Categories[category.Name] = category.Value.EnumerateArray().Select(d => d.GetString()!).ToList();
        if (setup is { } withCategories && withCategories.TryGetProperty("categories", out var extraCategories))
            foreach (var category in extraCategories.EnumerateObject())
                config.Categories[category.Name] = category.Value.EnumerateArray().Select(d => d.GetString()!).ToList();

        IEnumerable<JsonElement> Items(string name)
        {
            var items = fixture.TryGetProperty(name, out var f) ? f.EnumerateArray().ToList() : [];
            if (setup is { } s && s.TryGetProperty(name, out var extra))
                items.AddRange(extra.EnumerateArray());
            return items;
        }

        foreach (var mc in Items("memberCategories"))
        {
            config.MemberCategories.Add(new MemberCategory(
                mc.GetProperty("member").GetString()!, mc.GetProperty("category").GetString()!, ParseAction(mc.GetProperty("action"))));
        }
        foreach (var rule in Items("rules"))
            config.Rules.Add(ParseRule(rule, config.TimeZone));
        return config;
    }

    public static Rule ParseRule(JsonElement e, TimeZoneInfo zone) => new(
        e.GetProperty("id").GetString()!,
        e.TryGetProperty("member", out var member) ? member.GetString() : null,
        e.GetProperty("pattern").GetString()!,
        Enum.Parse<PatternType>(e.GetProperty("type").GetString()!, ignoreCase: true),
        ParseAction(e.GetProperty("action")),
        e.TryGetProperty("alwaysAllowed", out var always) && always.GetBoolean(),
        e.TryGetProperty("expiresAt", out var expires) ? ParseLocal(expires.GetString()!, zone, null) : null);

    /// <summary>"YYYY-MM-DDTHH:MM" or "HH:MM" (on <paramref name="day"/>), local wall-clock time in the fixture's zone.</summary>
    public static DateTimeOffset ParseLocal(string text, TimeZoneInfo zone, DateOnly? day)
    {
        var local = text.Contains('T')
            ? DateTime.ParseExact(text, "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)
            : day!.Value.ToDateTime(TimeOnly.ParseExact(text, "HH:mm", CultureInfo.InvariantCulture));
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static RuleAction ParseAction(JsonElement e) => Enum.Parse<RuleAction>(e.GetString()!, ignoreCase: true);

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

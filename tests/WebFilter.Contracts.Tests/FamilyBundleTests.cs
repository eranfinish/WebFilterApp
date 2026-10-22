using WebFilter.Contracts.Hub;

namespace WebFilter.Contracts.Tests;

public class FamilyBundleTests
{
    private static string FixturesDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx"))) return Path.Combine(dir.FullName, "tests", "simulation", "fixtures");
        throw new InvalidOperationException("WebFilter.slnx not found");
    }

    [Fact]
    public void Every_simulation_fixture_is_a_valid_bundle()
    {
        foreach (var file in Directory.GetFiles(FixturesDir(), "*.json"))
        {
            var bundle = FamilyBundleJson.Deserialize(File.ReadAllText(file));
            Assert.False(string.IsNullOrEmpty(bundle.TimeZone));
            Assert.NotEmpty(bundle.Members);
            Assert.NotEmpty(bundle.Devices);
            Assert.All(bundle.Schedules, s => Assert.Matches(@"^\d{2}:\d{2}$", s.Start));
        }
    }

    [Fact]
    public void Bundle_round_trips()
    {
        var bundle = FamilyBundleJson.Deserialize(File.ReadAllText(Path.Combine(FixturesDir(), "family-base.json"))) with { Version = 12 };
        var json = FamilyBundleJson.Serialize(bundle);
        var back = FamilyBundleJson.Deserialize(json);
        Assert.Equal(12, back.Version);
        Assert.Equal(json, FamilyBundleJson.Serialize(back));
        Assert.Contains("\"timeZone\":\"Asia/Jerusalem\"", json);
    }

    /// <summary>PROTECTION-SPEC §1–§2: an older bundle loads with the flags off and the Windows features at their defaults.</summary>
    [Fact]
    public void An_older_bundle_has_the_flags_off_and_the_default_windows_features()
    {
        var bundle = FamilyBundleJson.Deserialize(File.ReadAllText(Path.Combine(FixturesDir(), "family-base.json")));
        Assert.All(bundle.Members, m => Assert.Equal((false, false, false, false), (m.BlockImageSearch, m.JumpGuard, m.BlockYouTubeShorts, m.LogSearches)));
        Assert.Null(bundle.WindowsFeatures);

        var partial = FamilyBundleJson.Deserialize("""{"timeZone":"Asia/Jerusalem","windowsFeatures":{"oneDrive":false}}""");
        Assert.Equal(new WindowsFeaturesDto(NotificationsAndSync: true, OneDrive: false, WebSearchInStart: false), partial.WindowsFeatures);
    }

    [Fact]
    public void Protection_settings_use_the_spec_wire_names_and_round_trip()
    {
        var bundle = new FamilyBundle
        {
            TimeZone = "Asia/Jerusalem",
            Members = [new MemberDto("noa", "נועה", "child", 8, "allowlist", "allowlist", BlockImageSearch: true, JumpGuard: true, BlockYouTubeShorts: true, LogSearches: true)],
            WindowsFeatures = new WindowsFeaturesDto(NotificationsAndSync: false, OneDrive: true, WebSearchInStart: true),
        };
        var json = FamilyBundleJson.Serialize(bundle);

        Assert.Contains("\"blockImageSearch\":true,\"jumpGuard\":true,\"blockYouTubeShorts\":true,\"logSearches\":true", json);
        Assert.Contains("\"windowsFeatures\":{\"notificationsAndSync\":false,\"oneDrive\":true,\"webSearchInStart\":true}", json);
        var back = FamilyBundleJson.Deserialize(json);
        Assert.Equal(bundle.Members, back.Members);
        Assert.Equal(bundle.WindowsFeatures, back.WindowsFeatures);
    }
}

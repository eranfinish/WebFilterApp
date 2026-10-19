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
}

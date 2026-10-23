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
        Assert.All(bundle.Members, m => Assert.Equal((false, false, false, false, false), (m.BlockImageSearch, m.JumpGuard, m.BlockYouTubeShorts, m.LogSearches, m.BlockAds)));
        Assert.Null(bundle.WindowsFeatures);

        var partial = FamilyBundleJson.Deserialize("""{"timeZone":"Asia/Jerusalem","windowsFeatures":{"oneDrive":false}}""");
        Assert.Equal(new WindowsFeaturesDto(NotificationsAndSync: true, OneDrive: false, WebSearchInStart: false), partial.WindowsFeatures);
    }

    /// <summary>
    /// PROTECTION-SPEC §13: <c>baseline: { blockAdult, jumpGuard, blockAds, safeSearch }</c> round-trips; an older bundle has
    /// none (all on), and a switch left out of the section is on.
    /// </summary>
    [Fact]
    public void The_baseline_uses_the_spec_wire_names_and_is_on_when_missing()
    {
        var older = FamilyBundleJson.Deserialize(File.ReadAllText(Path.Combine(FixturesDir(), "family-base.json")));
        Assert.Null(older.Baseline);
        Assert.Equal(new BaselineDto(BlockAdult: true, JumpGuard: true, BlockAds: true, SafeSearch: true), new BaselineDto());

        var partial = FamilyBundleJson.Deserialize("""{"timeZone":"Asia/Jerusalem","baseline":{"jumpGuard":false}}""");
        Assert.Equal(new BaselineDto(BlockAdult: true, JumpGuard: false, BlockAds: true, SafeSearch: true), partial.Baseline);
        var empty = FamilyBundleJson.Deserialize("""{"timeZone":"Asia/Jerusalem","baseline":{}}""");
        Assert.Equal(new BaselineDto(), empty.Baseline);

        var bundle = new FamilyBundle { TimeZone = "Asia/Jerusalem", Baseline = new BaselineDto(BlockAdult: false, JumpGuard: true, BlockAds: false, SafeSearch: true) };
        var json = FamilyBundleJson.Serialize(bundle);
        Assert.Contains("\"baseline\":{\"blockAdult\":false,\"jumpGuard\":true,\"blockAds\":false,\"safeSearch\":true}", json);
        Assert.Equal(bundle.Baseline, FamilyBundleJson.Deserialize(json).Baseline);
        Assert.DoesNotContain("baseline", FamilyBundleJson.Serialize(older), StringComparison.Ordinal);
    }

    /// <summary>An account the parents kept as a guest: <c>guest: true</c> on the wire, left out when false, false when missing.</summary>
    [Fact]
    public void The_guest_flag_round_trips_and_is_left_out_when_false()
    {
        var bundle = new FamilyBundle
        {
            TimeZone = "Asia/Jerusalem",
            Devices = [new DeviceDto("pc-kids", "windows", "agent", [new AccountDto("S-1", "yinon", null, false, Guest: true), new AccountDto("S-2", "noa", "noa", false)])],
        };
        var json = FamilyBundleJson.Serialize(bundle);

        Assert.Contains("""{"accountId":"S-1","name":"yinon","isAdmin":false,"guest":true}""", json);
        Assert.Contains("""{"accountId":"S-2","name":"noa","member":"noa","isAdmin":false}""", json);
        Assert.Equal(bundle.Devices[0].Accounts, FamilyBundleJson.Deserialize(json).Devices[0].Accounts);

        var fixture = FamilyBundleJson.Deserialize(File.ReadAllText(Path.Combine(FixturesDir(), "family-base.json")));
        Assert.All(fixture.Devices.SelectMany(d => d.Accounts), a => Assert.False(a.Guest));
        Assert.DoesNotContain("guest", FamilyBundleJson.Serialize(fixture), StringComparison.Ordinal);
    }

    [Fact]
    public void Protection_settings_use_the_spec_wire_names_and_round_trip()
    {
        var bundle = new FamilyBundle
        {
            TimeZone = "Asia/Jerusalem",
            Members = [new MemberDto("noa", "נועה", "child", 8, "allowlist", "allowlist", BlockImageSearch: true, JumpGuard: true, BlockYouTubeShorts: true, LogSearches: true, BlockAds: true)],
            WindowsFeatures = new WindowsFeaturesDto(NotificationsAndSync: false, OneDrive: true, WebSearchInStart: true),
        };
        var json = FamilyBundleJson.Serialize(bundle);

        Assert.Contains("\"blockImageSearch\":true,\"jumpGuard\":true,\"blockYouTubeShorts\":true,\"logSearches\":true,\"blockAds\":true", json);
        Assert.Contains("\"windowsFeatures\":{\"notificationsAndSync\":false,\"oneDrive\":true,\"webSearchInStart\":true}", json);
        var back = FamilyBundleJson.Deserialize(json);
        Assert.Equal(bundle.Members, back.Members);
        Assert.Equal(bundle.WindowsFeatures, back.WindowsFeatures);
    }
}

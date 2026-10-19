using System.Text.Json.Nodes;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;

namespace WebFilter.Core.Tests.Engine;

public class SettingsValidatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T15:00:00+02:00");

    [Fact]
    public void The_fixture_is_valid() => Assert.Null(SettingsValidator.Validate(Fixture()));

    public static TheoryData<string, Action<FamilyConfig>> Broken() => new()
    {
        { "Two family members have the id 'noa'.", c => c.Members.Add(c.Members[0]) },
        { "Family member 'noa' has no name.", c => c.Members[0] = c.Members[0] with { DisplayName = " " } },
        { "Account 'savta' on 'pc-kids' refers to unknown member 'savta'.", c => c.Members.RemoveAll(m => m.Id == "savta") },
        { "Rule 'r-x' refers to unknown member 'ghost'.", c => c.Rules.Add(new Rule("r-x", "ghost", "example.com", PatternType.Domain, RuleAction.Allow)) },
        { "App rule 'a-x' refers to unknown member 'ghost'.", c => c.AppRules.Add(new AppRule("a-x", "ghost", AppMatchType.Path, @"C:\x.exe", RuleAction.Allow)) },
        { "A schedule refers to unknown member 'ghost'.", c => c.Schedules.Add(new Schedule("ghost", new HashSet<DayOfWeek> { DayOfWeek.Monday }, 60, 120)) },
        { "A daily limit refers to unknown member 'ghost'.", c => c.DailyLimits.Add(new DailyLimit("ghost", new HashSet<DayOfWeek> { DayOfWeek.Monday }, 60)) },
        { "YouTube channel 'UCx' refers to unknown member 'ghost'.", c => c.YouTubeChannels.Add(new YouTubeChannel("UCx", null, null, "ghost", RuleAction.Allow)) },
        { "Category 'social' refers to unknown member 'ghost'.", c => c.MemberCategories.Add(new MemberCategory("ghost", "social", RuleAction.Block)) },
        { "Two rules have the id 'r-school'.", c => c.Rules.Add(c.Rules[0] with { Pattern = "other.example" }) },
        { "Two app rules have the id 'a-school'.", c => c.AppRules.Add(c.AppRules[0]) },
        { "A schedule of 'noa' does not start before it ends within one day.", c => c.Schedules.Add(new Schedule("noa", new HashSet<DayOfWeek> { DayOfWeek.Monday }, 600, 600)) },
        { "A daily limit of 'noa' is not between 0 and 1440 minutes.", c => c.DailyLimits.Add(new DailyLimit("noa", new HashSet<DayOfWeek> { DayOfWeek.Monday }, 1441)) },
    };

    [Theory]
    [MemberData(nameof(Broken))]
    public void Broken_settings_are_refused_with_the_reason(string reason, Action<FamilyConfig> breakIt)
    {
        var config = Fixture();
        breakIt(config);
        Assert.Equal(reason, SettingsValidator.Validate(config));
    }

    [Fact]
    public void The_hub_keeps_its_settings_when_a_replacement_is_refused()
    {
        var hub = new HubCore(Fixture());
        var (before, version) = (hub.Config, hub.Version);
        var broken = hub.Config.Clone();
        broken.Members.RemoveAll(m => m.Id == "yoav");

        var ex = Assert.Throws<ArgumentException>(() => hub.ReplaceSettings(broken, Now));

        Assert.StartsWith("Account 'yoav' on 'pc-kids' refers to unknown member 'yoav'.", ex.Message);
        Assert.Same(before, hub.Config);
        Assert.Equal(version, hub.Version);
    }

    private static FamilyConfig Fixture() =>
        FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);
}

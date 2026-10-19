using System.Text.Json.Nodes;
using WebFilter.Core.Dnr;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;

namespace WebFilter.Core.Tests.Engine;

public class RuleValidatorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T15:00:00+02:00");

    [Theory]
    [InlineData("example.com", PatternType.Domain)]
    [InlineData("*.example.com", PatternType.Wildcard)]
    [InlineData("*.a.b.c.d.e.f.g.h", PatternType.Wildcard)]
    [InlineData("reddit.com/r/learn", PatternType.Prefix)]
    [InlineData(@"^https?://([a-z0-9-]+\.)*casino\.example/", PatternType.Regex)]
    public void Valid_rules_pass(string pattern, PatternType type) =>
        Assert.Null(RuleValidator.Validate(new Rule("r", "noa", pattern, type, RuleAction.Block)));

    [Theory]
    [InlineData("not a domain", PatternType.Domain)]
    [InlineData("*.a.b.c.d.e.f.g.h.i", PatternType.Wildcard)]
    [InlineData(@"(?=a)b", PatternType.Regex)]
    [InlineData(@"(a)\1", PatternType.Regex)]
    public void Invalid_or_too_large_rules_are_refused(string pattern, PatternType type) =>
        Assert.NotNull(RuleValidator.Validate(new Rule("r", "noa", pattern, type, RuleAction.Block)));

    [Fact]
    public void A_prefix_path_over_the_browser_limit_is_refused()
    {
        var fits = "example.com/" + new string('a', DnrLimits.MaxPrefixPathLength - 1);
        Assert.Null(RuleValidator.Validate(new Rule("r", "noa", fits, PatternType.Prefix, RuleAction.Allow)));
        Assert.Contains("80", RuleValidator.Validate(new Rule("r", "noa", fits + "b", PatternType.Prefix, RuleAction.Allow)));
    }

    [Fact]
    public void The_hub_refuses_an_invalid_rule_and_keeps_its_settings()
    {
        var hub = new HubCore(Fixture());
        var version = hub.Version;
        var ex = Assert.Throws<ArgumentException>(() => hub.AddRule(new Rule("bad", "noa", @"(?<=x)y", PatternType.Regex, RuleAction.Block), Now));
        Assert.Contains("'bad'", ex.Message);
        Assert.DoesNotContain(hub.Config.Rules, r => r.Id == "bad");
        Assert.Equal(version, hub.Version);
    }

    [Fact]
    public void Replacing_settings_checks_only_new_or_changed_rules()
    {
        var hub = new HubCore(Fixture());
        var withBad = hub.Config.Clone();
        withBad.Rules.Add(new Rule("bad", "noa", "*.a.b.c.d.e.f.g.h.i", PatternType.Wildcard, RuleAction.Allow));
        Assert.Throws<ArgumentException>(() => hub.ReplaceSettings(withBad, Now));

        var unchanged = hub.Config.Clone();
        hub.ReplaceSettings(unchanged, Now);
        Assert.Same(unchanged, hub.Config);
    }

    [Fact]
    public void Approving_a_long_page_creates_a_prefix_rule_that_compiles()
    {
        var hub = new HubCore(Fixture());
        var url = "https://www.example.com/articles/" + new string('x', 120) + "/page?id=1";
        var draft = new AccessRequestDraft("noa", "pc-kids", RequestKind.Url, url,
            HubCore.TargetKeyFor(RequestKind.Url, url, DateOnly.FromDateTime(Now.DateTime)), null, null, Now);
        Assert.Equal(RequestResult.Created, hub.CreateRequest(draft, out var request));

        hub.Approve(request!.Id, ApproveScope.Page, ApprovalDuration.For(60), null, Now);

        var rule = hub.Config.Rules.Single(r => r.Id == request.CreatedRuleId);
        Assert.Equal(PatternType.Prefix, rule.Type);
        Assert.Equal("www.example.com/articles/", rule.Pattern);
        Assert.Null(RuleValidator.Validate(rule));
        DnrCompiler.Compile(EffectiveUrlPolicy.For(hub.Config, "noa", Now), new DnrCompileOptions("abcdefghijklmnopabcdefghijklmnop"));
    }

    private static FamilyConfig Fixture() =>
        FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);
}

using System.Text.Json;
using WebFilter.Core.Decisions;
using WebFilter.Core.Model;
using WebFilter.Core.Urls;

namespace WebFilter.Core.Tests.Urls;

/// <summary>
/// Runs the URL cases of rules.json and normalization.json (checkUrls and navigate steps) for restricted members
/// through UrlNormalizer + RuleEvaluator. Time is within every member's window in these suites, so the rule
/// evaluator alone decides. RULE-010 (unrestricted adults) is the engine's job and is skipped.
/// </summary>
public class RuleScenarioTests
{
    private static readonly string[] Suites = ["rules.json", "normalization.json"];
    private static readonly HashSet<string> SkippedScenarios = ["RULE-010"];

    public static TheoryData<string, string, int, string, string, string, string?, string?> Cases()
    {
        var data = new TheoryData<string, string, int, string, string, string, string?, string?>();
        foreach (var suite in Suites)
        {
            foreach (var scenario in ScenarioData.Scenarios(suite))
            {
                var id = scenario.GetProperty("id").GetString()!;
                if (SkippedScenarios.Contains(id))
                    continue;

                var config = ScenarioData.BuildConfig(suite, scenario);
                var steps = scenario.GetProperty("steps").EnumerateArray().ToList();
                for (var i = 0; i < steps.Count; i++)
                {
                    var step = steps[i];
                    var kind = step.GetProperty("do").GetString();
                    if (kind is not ("checkUrls" or "navigate"))
                        continue;
                    var member = step.GetProperty("member").GetString()!;
                    if (!config.Member(member).IsRestricted)
                        continue;

                    List<JsonElement> cases = kind == "navigate" ? [step] : step.GetProperty("cases").EnumerateArray().ToList();
                    foreach (var c in cases)
                    {
                        var expect = c.GetProperty("expect");
                        data.Add(suite, id, i, member, c.GetProperty("url").GetString()!,
                            expect.GetProperty("decision").GetString()!,
                            ScenarioData.OptString(expect, "reason"),
                            ScenarioData.OptString(expect, "ruleId"));
                    }
                }
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Url_DecidesAsScenarioExpects(string suite, string scenarioId, int step, string member, string url,
        string decision, string? reason, string? ruleId)
    {
        var expectedDecision = ScenarioData.ParseEnum<Decision>(decision);
        if (expectedDecision == Decision.NotApplicable)
        {
            Assert.False(UrlNormalizer.IsWebUrl(url), $"{url} should not be a web URL");
            Assert.False(UrlNormalizer.TryNormalize(url, out _));
            return;
        }

        var (config, now) = StateAt(suite, scenarioId, step);
        Assert.True(UrlNormalizer.TryNormalize(url, out var normalized), $"{url} should normalize");
        var policy = EffectiveUrlPolicy.For(config, member, now);

        var result = RuleEvaluator.Evaluate(policy, normalized);

        var expected = $"{expectedDecision}/{reason}" + (ruleId is null ? "" : $"/{ruleId}");
        var actual = $"{result.Decision}/{result.Reason.ToWire()}" + (ruleId is null ? "" : $"/{result.RuleId}");
        Assert.True(expected == actual, $"{scenarioId} {member} {url} (normalized {normalized}): expected {expected}, got {actual}");
    }

    /// <summary>Config and clock right before the given step: replays earlier admin rule changes and every <c>at</c>.</summary>
    private static (FamilyConfig Config, DateTimeOffset Now) StateAt(string suite, string scenarioId, int stepIndex)
    {
        var scenario = ScenarioData.Scenario(suite, scenarioId);
        var config = ScenarioData.BuildConfig(suite, scenario);
        var now = ScenarioData.ParseLocal(scenario.GetProperty("start").GetString()!, config.TimeZone);
        var steps = scenario.GetProperty("steps").EnumerateArray().ToList();

        for (var i = 0; i <= stepIndex; i++)
        {
            var step = steps[i];
            if (ScenarioData.OptString(step, "at") is { } at)
                now = ScenarioData.ParseAt(at, now, config.TimeZone);
            if (i == stepIndex)
                break;

            switch (step.GetProperty("do").GetString())
            {
                case "checkUrls" or "navigate":
                    break;
                case "admin":
                    ApplyAdmin(config, step);
                    break;
                case var other:
                    throw new NotSupportedException($"{scenarioId}: step '{other}' is not replayed by the URL tests");
            }
        }
        return (config, now);
    }

    private static void ApplyAdmin(FamilyConfig config, JsonElement step)
    {
        switch (step.GetProperty("action").GetString())
        {
            case "removeRule":
                config.Rules.RemoveAll(r => r.Id == step.GetProperty("ruleId").GetString());
                break;
            case "addRule":
                config.Rules.Add(ScenarioData.ParseRule(step.GetProperty("rule"), config.TimeZone));
                break;
            case var other:
                throw new NotSupportedException($"admin action '{other}' is not replayed by the URL tests");
        }
    }
}

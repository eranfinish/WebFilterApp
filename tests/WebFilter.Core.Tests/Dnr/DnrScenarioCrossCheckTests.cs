using System.Text.Json;
using WebFilter.Core.Dnr;
using WebFilter.Core.Model;

namespace WebFilter.Core.Tests.Dnr;

/// <summary>
/// For every URL check in the rule, normalization, YouTube-off, image-search, AI Mode and baseline scenarios, the compiled DNR
/// rules, run through <see cref="DnrSimulator"/>, must reach the scenario's decision (allow vs block; redirect counts as block).
/// Unrestricted members get the family's baseline policy (PROTECTION-SPEC §13), as the extension does.
/// </summary>
public class DnrScenarioCrossCheckTests
{
    private static readonly string[] Files = ["rules.json", "normalization.json", "youtube-decisions.json", "image-search.json", "ai-mode.json", "baseline.json"];

    /// <summary>RULE-011: non-web schemes (not filtered by DNR).</summary>
    private static readonly HashSet<string> SkippedScenarios = ["RULE-011"];

    internal sealed record Case(string Label, EffectiveUrlPolicy Policy, string Url, string ResourceType, DnrOutcome Expected);

    private static readonly Lazy<(IReadOnlyDictionary<string, Case> Cases, IReadOnlyList<string> Skipped)> Catalog = new(Load);

    public static TheoryData<string> CaseLabels()
    {
        var data = new TheoryData<string>();
        foreach (var label in Catalog.Value.Cases.Keys)
            data.Add(label);
        return data;
    }

    [Theory]
    [MemberData(nameof(CaseLabels))]
    public void CompiledRules_ReachTheScenarioDecision(string label)
    {
        var c = Catalog.Value.Cases[label];
        var rules = DnrCompiler.Compile(c.Policy, DnrTestPolicies.Options);
        var url = ChromeUrl.Parse(c.Url);
        var winner = DnrSimulator.Match(rules.DynamicRules, url, c.ResourceType);
        var actual = DnrSimulator.Evaluate(rules.DynamicRules, url, c.ResourceType);
        Assert.True(c.Expected == actual,
            $"{label}: expected {c.Expected}, DNR gives {actual} (canonical URL {url.Spec}; winning rule {(winner is null ? "none" : JsonSerializer.Serialize(winner))})");
    }

    [Fact]
    public void Catalog_CoversTheExpectedCases_AndSkipsOnlyKnownOnes()
    {
        var (cases, skipped) = Catalog.Value;
        Assert.True(cases.Count >= 69, $"only {cases.Count} cases cross-checked");
        // IMG-004 runs homework and a pause, which only the engine models; ExtensionPolicyTests covers those states for DNR, and
        // BaselineProtectionTests an administrator linked to nobody (an account step) and a pause set for a parent.
        Assert.All(skipped, s => Assert.Matches(
            @"^RULE-011 |^IMG-004 step 1\+ \(admin action not modelled\)$|^BASE-\d{3} step \d+(\+ \(admin action not modelled\)| \(an account, not a member\))$", s));
    }

    private static (IReadOnlyDictionary<string, Case>, IReadOnlyList<string>) Load()
    {
        var cases = new Dictionary<string, Case>(StringComparer.Ordinal);
        var skipped = new List<string>();
        foreach (var file in Files)
        {
            foreach (var scenario in ScenarioData.Scenarios(file))
            {
                var id = scenario.GetProperty("id").GetString()!;
                if (SkippedScenarios.Contains(id))
                {
                    skipped.Add($"{id} (by design)");
                    continue;
                }
                var setup = scenario.TryGetProperty("setup", out var s) ? s : (JsonElement?)null;
                var config = ScenarioData.BuildConfig(setup);
                var now = ScenarioData.ParseLocal(scenario.GetProperty("start").GetString()!, config.TimeZone, null);
                var day = DateOnly.FromDateTime(now.DateTime);

                var stepIndex = 0;
                foreach (var step in scenario.GetProperty("steps").EnumerateArray())
                {
                    stepIndex++;
                    if (step.TryGetProperty("at", out var at))
                        now = ScenarioData.ParseLocal(at.GetString()!, config.TimeZone, day);
                    var kind = step.GetProperty("do").GetString();
                    if (kind == "admin")
                    {
                        if (!ApplyAdmin(config, step))
                        {
                            skipped.Add($"{id} step {stepIndex}+ (admin action not modelled)");
                            break;
                        }
                        continue;
                    }
                    if (kind is not ("checkUrls" or "navigate"))
                        continue;

                    if (!step.TryGetProperty("member", out var member))
                    {
                        skipped.Add($"{id} step {stepIndex} (an account, not a member)");
                        continue;
                    }
                    var memberId = member.GetString()!;
                    // A parent's account gets the family's baseline protection (PROTECTION-SPEC §13), or nothing without blockAdult.
                    var policy = config.Member(memberId).IsRestricted || !config.Baseline.BlockAdult
                        ? EffectiveUrlPolicy.For(config, memberId, now)
                        : EffectiveUrlPolicy.ForBaseline(config, memberId, now);
                    List<JsonElement> checks = kind == "navigate" ? [step] : step.GetProperty("cases").EnumerateArray().ToList();
                    var caseIndex = 0;
                    foreach (var check in checks)
                    {
                        caseIndex++;
                        var url = check.GetProperty("url").GetString()!;
                        var decision = check.GetProperty("expect").GetProperty("decision").GetString();
                        var label = $"{id} {stepIndex}.{caseIndex} {memberId} {url}";
                        if (decision == "not_applicable")
                        {
                            skipped.Add(label);
                            continue;
                        }
                        var frame = check.TryGetProperty("frame", out var f) && f.GetString() == "sub" ? "sub_frame" : "main_frame";
                        cases[label] = new Case(label, policy, url, frame, decision == "allow" ? DnrOutcome.Allow : DnrOutcome.Block);
                    }
                }
            }
        }
        return (cases, skipped);
    }

    private static bool ApplyAdmin(FamilyConfig config, JsonElement step)
    {
        switch (step.GetProperty("action").GetString())
        {
            case "addRule":
                config.Rules.Add(ScenarioData.ParseRule(step.GetProperty("rule"), config.TimeZone));
                return true;
            case "removeRule":
                var id = step.GetProperty("ruleId").GetString();
                config.Rules.RemoveAll(r => r.Id == id);
                return true;
            default:
                return false;
        }
    }
}

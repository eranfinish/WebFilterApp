using System.Text.Json.Nodes;

namespace WebFilter.Core.Tests.Simulation;

/// <summary>Runs every scenario marked for the "engine" layer and the "windows" platform (docs/SIMULATION-PLAN.md §2.1).</summary>
public class EngineScenarioTests
{
    internal static readonly string SimulationDir = Path.Combine(RepoRoot(), "tests", "simulation");
    private static readonly Lazy<Dictionary<string, (JsonObject Scenario, JsonObject Fixture)>> Catalog = new(Load);

    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var id in Catalog.Value.Keys.Order(StringComparer.Ordinal)) data.Add(id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Scenario(string id)
    {
        var (scenario, fixture) = Catalog.Value[id];
        var failures = new ScenarioRunner(fixture, scenario).Run(scenario["steps"]!.AsArray());
        Assert.True(failures.Count == 0, $"{id} — {scenario["title"]}{Environment.NewLine}  " + string.Join(Environment.NewLine + "  ", failures));
    }

    private static Dictionary<string, (JsonObject, JsonObject)> Load()
    {
        var fixtures = new Dictionary<string, JsonObject>();
        var result = new Dictionary<string, (JsonObject, JsonObject)>();
        foreach (var file in Directory.GetFiles(Path.Combine(SimulationDir, "scenarios"), "*.json", SearchOption.AllDirectories))
        {
            var suite = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var fixtureName = suite["fixture"]?.GetValue<string>() ?? "family-base";
            if (!fixtures.TryGetValue(fixtureName, out var fixture))
                fixtures[fixtureName] = fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(SimulationDir, "fixtures", fixtureName + ".json")))!.AsObject();
            foreach (var node in suite["scenarios"]!.AsArray())
            {
                var scenario = node!.AsObject();
                var layers = (scenario["layers"] ?? suite["layers"])!.AsArray().Select(l => l!.GetValue<string>());
                var platforms = (scenario["platforms"] ?? suite["platforms"])!.AsArray().Select(p => p!.GetValue<string>());
                if (layers.Contains("engine") && platforms.Contains("windows"))
                    result[scenario["id"]!.GetValue<string>()] = (scenario, fixture);
            }
        }
        return result;
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx"))) return dir.FullName;
        throw new InvalidOperationException("WebFilter.slnx not found");
    }
}

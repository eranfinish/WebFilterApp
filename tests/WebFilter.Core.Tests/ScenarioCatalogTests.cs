using System.Text.Json;

namespace WebFilter.Core.Tests;

// Guards the simulation catalog until the level-1 runner (M1) executes it:
// every scenario file parses, and scenario ids are unique across files.
public class ScenarioCatalogTests
{
    private static readonly string ScenariosDir = Path.Combine(FindRepoRoot(), "tests", "simulation", "scenarios");

    [Fact]
    public void Catalog_HasScenarios_WithUniqueIds()
    {
        var files = Directory.GetFiles(ScenariosDir, "*.json", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var seen = new Dictionary<string, string>();
        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var scenario in doc.RootElement.GetProperty("scenarios").EnumerateArray())
            {
                var id = scenario.GetProperty("id").GetString()!;
                Assert.False(seen.ContainsKey(id), $"Duplicate scenario id {id} in {file} and {seen.GetValueOrDefault(id)}");
                seen[id] = file;
            }
        }
        Assert.True(seen.Count >= 50, $"Expected the full catalog, found {seen.Count} scenarios");
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

using System.IO;
using System.Text.Json;

namespace WebFilter.Admin.Services;

/// <summary>A ready-made category: registrable domains matched with their subdomains (data/starter-lists).</summary>
public sealed record StarterList(string Category, IReadOnlyList<string> Domains);

/// <summary>The category lists shipped with the product (data/starter-lists, embedded at build), offered in the setup wizard.</summary>
public static class StarterLists
{
    /// <summary>In the order the wizard offers them.</summary>
    public static readonly string[] Names = ["educational", "social", "gaming"];

    private static readonly Lazy<IReadOnlyList<StarterList>> Lists = new(Load);

    public static IReadOnlyList<StarterList> All => Lists.Value;

    private static IReadOnlyList<StarterList> Load()
    {
        var assembly = typeof(StarterLists).Assembly;
        return [.. Names.Select(name =>
        {
            using var stream = assembly.GetManifestResourceStream($"StarterLists.{name}.json")
                               ?? throw new InvalidOperationException($"The starter list '{name}' is not embedded.");
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            var category = root.GetProperty("category").GetString() ?? throw new InvalidDataException($"Starter list '{name}' has no category.");
            return new StarterList(category, [.. root.GetProperty("domains").EnumerateArray().Select(d => d.GetString()!)]);
        })];
    }
}

using System.IO;
using System.Text.Json;

namespace WebFilter.Admin.Services;

/// <summary>A ready-made category: registrable domains matched with their subdomains (data/starter-lists).</summary>
public sealed record StarterList(string Category, IReadOnlyList<string> Domains);

/// <summary>
/// The category lists shipped with the product (data/starter-lists, embedded at build), offered in the setup wizard and on
/// the member page. A list a parent picks is copied into the family's categories; the licence notices travel inside the
/// embedded files, so the copy needs none of its own.
/// </summary>
public static class StarterLists
{
    /// <summary>Sign-in and school platforms (PROTECTION-SPEC §3); allowing it for a member also adds <c>SchoolSignIn</c>'s rules.</summary>
    public const string SchoolTools = "school-tools";

    /// <summary>In the order the wizard and the member page offer them: the first three, then PROTECTION-SPEC §11's.</summary>
    public static readonly string[] Names =
        ["educational", "social", "gaming", SchoolTools, "adult", "gambling", "proxies", "ai-chat", "unfiltered-search", "ads-popups"];

    private static readonly Lazy<IReadOnlyList<StarterList>> Lists = new(Load);

    public static IReadOnlyList<StarterList> All => Lists.Value;

    /// <summary>The shipped list for <paramref name="category"/>, or null when it is the family's own category.</summary>
    public static StarterList? Find(string? category) =>
        category is null ? null : All.FirstOrDefault(l => string.Equals(l.Category, category, StringComparison.OrdinalIgnoreCase));

    /// <summary>The family's categories plus <paramref name="lists"/>; a category the family already has keeps its own domains.</summary>
    public static Dictionary<string, IReadOnlyList<string>> AddTo(IReadOnlyDictionary<string, IReadOnlyList<string>> categories, IEnumerable<StarterList> lists)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(lists);
        var result = new Dictionary<string, IReadOnlyList<string>>(categories, StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists) result.TryAdd(list.Category, list.Domains);
        return result;
    }

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

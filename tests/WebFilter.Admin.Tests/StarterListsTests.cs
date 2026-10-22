using System.Text.Json;
using WebFilter.Admin.Services;
using WebFilter.Admin.ViewModels;

namespace WebFilter.Admin.Tests;

/// <summary>data/starter-lists, as embedded in the admin app: usable as category domains, and worded.</summary>
public sealed class StarterListsTests
{
    [Fact]
    public void The_lists_are_embedded_in_order_and_worded()
    {
        // The first three, then PROTECTION-SPEC §11's titles in its order.
        Assert.Equal(["educational", "social", "gaming", "school-tools", "adult", "gambling", "proxies", "ai-chat", "unfiltered-search", "ads-popups"],
            StarterLists.All.Select(l => l.Category));
        Assert.Equal(["כלי בית ספר", "תוכן למבוגרים", "הימורים", "פרוקסי ועוקפי חסימות", "צ'אטבוטים", "מנועי חיפוש לא מסוננים", "פרסומות וחלונות קופצים"],
            StarterLists.All.Skip(3).Select(l => Texts.Code("Category_", l.Category)));
        Assert.All(StarterLists.All, l => Assert.NotEqual(l.Category, Texts.Code("Category_", l.Category)));
        Assert.All(StarterLists.All, l => Assert.True(l.Domains.Count >= 10, $"{l.Category}: {l.Domains.Count} sites"));
    }

    [Fact]
    public void The_learning_list_is_the_category_homework_mode_allows()
    {
        // WebFilter.Core's FamilyConfig.EducationalCategory: homework mode allows these sites.
        Assert.Equal(Core.Model.FamilyConfig.EducationalCategory, StarterLists.All[0].Category);
    }

    [Fact]
    public void Every_entry_is_a_site_as_a_domain_rule_stores_it_and_appears_once()
    {
        var all = StarterLists.All.SelectMany(l => l.Domains.Select(d => (l.Category, Domain: d))).ToList();
        foreach (var (category, domain) in all)
        {
            Assert.True(RulePatternValidator.TryNormalize(domain, "domain", out var stored, out var error), $"{category}: {domain}: {error}");
            Assert.Equal(stored, domain);
        }
        Assert.Equal(all.Count, all.Select(e => e.Domain).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("adult")]
    [InlineData("gambling")]
    [InlineData("proxies")]
    [InlineData("ads-popups")]
    public void A_sourced_list_carries_its_source_and_licence_in_the_embedded_file(string name)
    {
        // The notices travel with the file the app ships, so copying the domains into a family's categories needs none.
        using var stream = typeof(StarterLists).Assembly.GetManifestResourceStream($"StarterLists.{name}.json")!;
        using var json = JsonDocument.Parse(stream);
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("source").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("license").GetString()));
    }

    [Fact]
    public void Adding_lists_keeps_a_category_the_family_already_has()
    {
        var family = new Dictionary<string, IReadOnlyList<string>> { ["Educational"] = ["school.example"] };

        var merged = StarterLists.AddTo(family, [StarterLists.Find("educational")!, StarterLists.Find("school-tools")!]);

        Assert.Equal(["school.example"], merged["educational"]);
        Assert.Contains("classroom.google.com", merged["school-tools"]);
        Assert.Null(StarterLists.Find("homework-club"));
    }
}

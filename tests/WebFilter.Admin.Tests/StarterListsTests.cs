using WebFilter.Admin.Services;
using WebFilter.Admin.ViewModels;

namespace WebFilter.Admin.Tests;

/// <summary>data/starter-lists, as embedded in the admin app: usable as category domains, and worded.</summary>
public sealed class StarterListsTests
{
    [Fact]
    public void The_three_lists_are_embedded_and_worded()
    {
        Assert.Equal(["educational", "social", "gaming"], StarterLists.All.Select(l => l.Category));
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
}

using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.Tests;

/// <summary>PROTECTION-SPEC §11: the school sign-in rules follow the school tools list.</summary>
public sealed class SchoolSignInTests
{
    private static readonly string[] Patterns = ["www.google.com/a/", "www.google.com/recaptcha/"];

    private static FamilyBundle With(params MemberCategoryDto[] choices) => TestData.Family() with { MemberCategories = [.. TestData.Family().MemberCategories, .. choices] };

    [Fact]
    public void A_member_allowed_the_school_tools_brings_two_global_prefix_allow_rules()
    {
        var settings = SchoolSignIn.Apply(With(new MemberCategoryDto("noa", "school-tools", "allow")));

        var added = settings.Rules.Where(SchoolSignIn.IsOurs).ToList();
        Assert.Equal(Patterns, added.Select(r => r.Pattern));
        Assert.All(added, r =>
        {
            Assert.StartsWith("builtin-school-signin-", r.Id, StringComparison.Ordinal);
            Assert.Equal(("prefix", "allow", false), (r.Type, r.Action, r.AlwaysAllowed));
            Assert.Null(r.Member);
            Assert.Null(r.ExpiresAt);
            Assert.True(RulePatternValidator.TryNormalize(r.Pattern, r.Type, out var stored, out _) && stored == r.Pattern, r.Pattern);
        });
        Assert.Equal("r-school", settings.Rules[0].Id);
    }

    [Fact]
    public void Applying_again_adds_nothing()
    {
        var once = SchoolSignIn.Apply(With(new MemberCategoryDto("noa", "school-tools", "allow"), new MemberCategoryDto("yoav", "school-tools", "allow")));

        Assert.Same(once, SchoolSignIn.Apply(once));
        Assert.Equal(2, once.Rules.Count(SchoolSignIn.IsOurs));
    }

    [Fact]
    public void A_parents_own_rule_that_does_the_same_is_not_doubled_and_is_never_removed()
    {
        var own = new RuleDto("r-google-a", null, "WWW.google.com/a/", "prefix", "allow");
        var settings = With(new MemberCategoryDto("noa", "school-tools", "allow")) with { Rules = [own] };

        var applied = SchoolSignIn.Apply(settings);
        Assert.Equal(["r-google-a", "builtin-school-signin-recaptcha"], applied.Rules.Select(r => r.Id));

        var removed = SchoolSignIn.Apply(applied with { MemberCategories = [] });
        Assert.Equal([own], removed.Rules);
    }

    [Theory]
    [InlineData("block")]
    [InlineData("default")]
    public void Without_a_member_allowed_the_school_tools_the_rules_go(string action)
    {
        var withRules = SchoolSignIn.Apply(With(new MemberCategoryDto("noa", "school-tools", "allow")));

        var after = SchoolSignIn.Apply(withRules with
        {
            MemberCategories = [.. withRules.MemberCategories.Where(c => c.Category != "school-tools"), new MemberCategoryDto("noa", "school-tools", action)],
        });

        Assert.DoesNotContain(after.Rules, SchoolSignIn.IsOurs);
        Assert.Equal(["r-school"], after.Rules.Select(r => r.Id));
    }

    [Fact]
    public void A_choice_left_by_a_member_who_is_gone_does_not_count()
    {
        var settings = With(new MemberCategoryDto("someone-else", "school-tools", "allow"));

        Assert.False(SchoolSignIn.IsNeeded(settings));
        Assert.Same(settings, SchoolSignIn.Apply(settings));
    }
}

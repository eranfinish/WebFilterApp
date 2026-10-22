using WebFilter.Admin.Resources;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.Tests;

public sealed class RulesViewModelTests
{
    private readonly FakeAdminClient _client = new();
    private readonly FakeDialogs _dialogs = new();

    public RulesViewModelTests()
    {
        _client.Settings = TestData.Family() with
        {
            Rules =
            [
                new RuleDto("r-school", null, "mashov.info", "domain", "allow", AlwaysAllowed: true),
                new RuleDto("r-yt", "yoav", "youtube.com", "domain", "block", ExpiresAt: TestData.Now.AddHours(2)),
                new RuleDto("r-old", "noa", "old.example", "prefix", "allow", ExpiresAt: TestData.Now.AddHours(-1)),
            ],
        };
        // Like the service: every change bumps the settings version, so the page refetches them.
        _client.Reply = m =>
        {
            if (m is AddRule or RemoveRule) _client.Status = _client.Status with { SettingsVersion = _client.Status.SettingsVersion + 1 };
            return null;
        };
    }

    private async Task<RulesViewModel> LoadedAsync()
    {
        var vm = new RulesViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)), _dialogs) { NewRuleId = () => "r-test" };
        await vm.RefreshAsync();
        return vm;
    }

    [Fact]
    public async Task Lists_global_and_member_rules()
    {
        var vm = await LoadedAsync();

        Assert.Equal(["r-school", "r-yt", "r-old"], vm.Rules.Select(r => r.Id));
        var school = vm.Rules[0];
        Assert.Equal("כל המשפחה", school.AppliesToText);
        Assert.Equal("אתר (כולל תתי-דומיינים)", school.TypeText);
        Assert.Equal("מותר", school.ActionText);
        Assert.True(school.AlwaysAllowed);
        Assert.Equal("קבוע", school.ExpiryText);
        Assert.Equal("יואב", vm.Rules[1].AppliesToText);
        Assert.Equal("חסום", vm.Rules[1].ActionText);
        Assert.Equal("עד 18:00", vm.Rules[1].ExpiryText);
        Assert.True(vm.Rules[2].IsExpired);
        Assert.Equal("פג תוקף", vm.Rules[2].ExpiryText);
        Assert.Equal("דף או תיקייה באתר", vm.Rules[2].TypeText);
        Assert.Equal(["*", "", "noa", "yoav", "dana", "savta"], vm.FilterOptions.Select(o => o.Value));
        Assert.Equal(["", "noa", "yoav", "dana", "savta"], vm.AppliesToOptions.Select(o => o.Value));
    }

    [Fact]
    public async Task The_school_sign_in_rules_are_marked_as_the_apps_own()
    {
        _client.Settings = _client.Settings with { Rules = [.. _client.Settings.Rules, .. SchoolSignIn.Rules] };

        var vm = await LoadedAsync();

        Assert.Equal(["mashov.info", "www.google.com/a/", "www.google.com/recaptcha/"], vm.Rules.Where(r => r.Rule.Member is null).Select(r => r.Pattern));
        Assert.Equal([false, true, true], vm.Rules.Where(r => r.Rule.Member is null).Select(r => r.IsSchoolSignIn));
        Assert.Equal("דף או תיקייה באתר", vm.Rules.Single(r => r.Id == "builtin-school-signin-google-a").TypeText);
    }

    [Theory]
    [InlineData(RulesViewModel.AllRules, new[] { "r-school", "r-yt", "r-old" })]
    [InlineData(RulesViewModel.Global, new[] { "r-school" })]
    [InlineData("noa", new[] { "r-old" })]
    [InlineData("dana", new string[0])]
    public async Task Filters_by_whom_the_rule_applies_to(string filter, string[] expected)
    {
        var vm = await LoadedAsync();

        vm.SelectedFilter = filter;

        Assert.Equal(expected, vm.Rules.Select(r => r.Id));
        Assert.Equal(expected.Length == 0, vm.HasNoRules);
    }

    [Theory]
    [InlineData("", "domain", nameof(Strings.Validation_Empty))]
    [InlineData("   ", "prefix", nameof(Strings.Validation_Empty))]
    [InlineData("*.example.com", "domain", nameof(Strings.Validation_DomainStar))]
    [InlineData("example.com/games", "domain", nameof(Strings.Validation_DomainHasPath))]
    [InlineData("example.com", "wildcard", nameof(Strings.Validation_WildcardForm))]
    [InlineData("games.*.example.com", "wildcard", nameof(Strings.Validation_WildcardForm))]
    [InlineData("*.*.example.com", "wildcard", nameof(Strings.Validation_WildcardForm))]
    [InlineData("example.com", "prefix", nameof(Strings.Validation_PrefixForm))]
    [InlineData("(?<=a)b", "regex", nameof(Strings.Validation_RegexUnsupported))]
    [InlineData("(a", "regex", nameof(Strings.Validation_RegexInvalid))]
    [InlineData("mashov.info", "domain", nameof(Strings.Validation_Duplicate))]
    public async Task Invalid_patterns_are_explained_and_not_sent(string pattern, string type, string messageKey)
    {
        var vm = await LoadedAsync();
        vm.NewPattern = pattern;
        vm.NewType = type;
        vm.NewAction = "allow";

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal(Strings.ResourceManager.GetString(messageKey, Strings.Culture), vm.ValidationMessage);
        Assert.Empty(_client.SentOf<AddRule>());
    }

    [Fact]
    public async Task A_name_that_is_not_a_site_is_explained()
    {
        var vm = await LoadedAsync();
        vm.NewPattern = "games";

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal("\"games\" אינו שם אתר תקין. כתבו רק את שם האתר, למשל example.com.", Bidi.Plain(vm.ValidationMessage));
        Assert.Empty(_client.SentOf<AddRule>());
    }

    [Fact]
    public async Task Adding_a_member_rule_sends_it_and_reloads()
    {
        var vm = await LoadedAsync();
        vm.NewPattern = "  Games.Example.COM ";
        vm.NewType = "domain";
        vm.NewAction = "allow";
        vm.NewAppliesTo = "noa";
        vm.NewAlwaysAllowed = true;

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal(new AddRule(new RuleDto("r-test", "noa", "games.example.com", "domain", "allow", AlwaysAllowed: true)), _client.Single<AddRule>());
        Assert.Equal("הכלל עבור games.example.com נוסף.", Bidi.Plain(vm.StatusMessage));
        Assert.Equal("", vm.NewPattern);
        Assert.False(vm.NewAlwaysAllowed);
        Assert.Null(vm.ValidationMessage);
        Assert.Equal(2, _client.SentOf<GetSettings>().Count());
    }

    [Fact]
    public async Task Adding_a_global_block_rule_never_marks_it_always_allowed()
    {
        var vm = await LoadedAsync();
        vm.NewAction = "allow";
        vm.NewAlwaysAllowed = true;
        vm.NewAction = "block";
        Assert.False(vm.NewAlwaysAllowed);
        Assert.False(vm.CanBeAlwaysAllowed);

        vm.NewPattern = "*.Casino.example";
        vm.NewType = "wildcard";
        vm.NewAppliesTo = RulesViewModel.Global;
        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal(new AddRule(new RuleDto("r-test", null, "*.casino.example", "wildcard", "block")), _client.Single<AddRule>());
        // Marked left-to-right so the Hebrew sentence does not show it as "casino.example.*".
        Assert.Contains("‎*.casino.example‎", vm.StatusMessage);
    }

    [Fact]
    public async Task A_page_rule_keeps_its_path()
    {
        var vm = await LoadedAsync();
        vm.NewPattern = "https://Reddit.com/r/Learn";
        vm.NewType = "prefix";

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal("reddit.com/r/Learn", _client.Single<AddRule>().Rule.Pattern);
    }

    [Fact]
    public async Task New_rules_get_unique_ids_by_default()
    {
        var vm = new RulesViewModel(new FamilyContext(_client), _dialogs);
        var ids = Enumerable.Range(0, 50).Select(_ => vm.NewRuleId()).ToHashSet();

        Assert.Equal(50, ids.Count);
        Assert.All(ids, id => Assert.Matches("^r-[0-9a-f]{12}$", id));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Removing_asks_first_then_sends_RemoveRule()
    {
        var vm = await LoadedAsync();
        var rule = vm.Rules[1];

        _dialogs.Answer = false;
        await vm.RemoveCommand.ExecuteAsync(rule);
        Assert.Empty(_client.SentOf<RemoveRule>());
        Assert.Equal("להסיר את הכלל עבור youtube.com?", Bidi.Plain(Assert.Single(_dialogs.Asked)));

        _dialogs.Answer = true;
        await vm.RemoveCommand.ExecuteAsync(rule);
        Assert.Equal(new RemoveRule("r-yt"), _client.Single<RemoveRule>());
        Assert.Equal("הכלל עבור youtube.com הוסר.", Bidi.Plain(vm.StatusMessage));
    }

    [Fact]
    public async Task A_rule_the_service_rejects_keeps_the_form()
    {
        var vm = await LoadedAsync();
        _client.Reply = m => m is AddRule ? new AdminError("bad_request", "Requested value 'x' was not found.") : null;
        vm.NewPattern = "example.com";

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal("example.com", vm.NewPattern);
        Assert.Equal("שירות הסינון לא ביצע את הפעולה: Requested value 'x' was not found.", vm.ErrorMessage);
    }

    [Theory]
    [InlineData(RuleExpiry.Hour, "2026-10-19T17:00:00+03:00", "עד 17:00")]
    [InlineData(RuleExpiry.Today, "2026-10-20T00:00:00+03:00", "עד סוף היום")]
    [InlineData(RuleExpiry.Permanent, null, "קבוע")]
    public async Task A_new_rule_can_last_an_hour_or_until_the_end_of_the_day(string expiry, string? expiresAt, string listed)
    {
        var vm = await LoadedAsync();
        Assert.Equal(["קבוע", "לשעה", "עד סוף היום"], vm.ExpiryOptions.Select(o => o.Label));
        vm.NewPattern = "games.example";
        vm.NewAction = "allow";
        vm.NewAppliesTo = "noa";
        vm.NewExpiry = expiry;

        await vm.AddCommand.ExecuteAsync(null);

        var rule = _client.Single<AddRule>().Rule;
        Assert.Equal(expiresAt is null ? null : DateTimeOffset.Parse(expiresAt), rule.ExpiresAt);
        Assert.Equal(listed, new RuleItemViewModel(rule, vm.Context, TestData.Now).ExpiryText);
        Assert.Equal(RuleExpiry.Permanent, vm.NewExpiry);
    }

    [Fact]
    public async Task The_end_of_the_day_follows_the_service_clock_and_the_family_time_zone()
    {
        // The service clock (a Simulation service) is past midnight of the family's day (UTC+3), while this PC's clock
        // still says 16:00 the day before.
        _client.Status = _client.Status with { Now = new DateTimeOffset(2026, 10, 19, 21, 30, 0, TimeSpan.Zero) };
        var vm = await LoadedAsync();
        vm.NewPattern = "games.example";
        vm.NewExpiry = RuleExpiry.Today;

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Equal(new DateTimeOffset(2026, 10, 21, 0, 0, 0, TimeSpan.FromHours(3)), _client.Single<AddRule>().Rule.ExpiresAt);
    }

    [Fact]
    public async Task An_expired_rule_does_not_stop_the_same_rule_from_being_added_again()
    {
        _client.Settings = _client.Settings with
        {
            Rules = [.. _client.Settings.Rules, new RuleDto("r-gone", "noa", "gone.example", "domain", "allow", ExpiresAt: TestData.Now.AddMinutes(-5))],
        };
        var vm = await LoadedAsync();
        vm.NewPattern = "gone.example";
        vm.NewAction = "allow";
        vm.NewAppliesTo = "noa";
        vm.NewExpiry = RuleExpiry.Hour;

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Null(vm.ValidationMessage);
        Assert.Equal("gone.example", _client.Single<AddRule>().Rule.Pattern);
    }

    [Fact]
    public async Task Editing_the_pattern_clears_the_validation_message()
    {
        var vm = await LoadedAsync();
        await vm.AddCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ValidationMessage);

        vm.NewPattern = "e";

        Assert.Null(vm.ValidationMessage);
    }
}

using WebFilter.Admin.Resources;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Tests;

/// <summary>The requirement checks (GetDiagnostics) in words, in the setup wizard and on the settings page.</summary>
public sealed class CheckItemTests
{
    private readonly FakeAdminClient _client = new();

    /// <summary>Every check and state WebFilter.Service's MachineChecks reports.</summary>
    public static TheoryData<string, string> Known() => new()
    {
        { "service", "ok" }, { "service", "warn" }, { "browsers", "ok" }, { "browsers", "fail" }, { "browser_policies", "ok" }, { "browser_policies", "warn" },
        { "extension", "ok" }, { "extension", "warn" }, { "extension", "fail" }, { "network", "ok" }, { "network", "warn" }, { "network", "fail" },
        { "hub_port", "ok" }, { "hub_port", "warn" }, { "onedrive", "ok" }, { "onedrive", "warn" }, { "onedrive", "none" },
    };

    [Theory]
    [MemberData(nameof(Known))]
    public void Every_check_has_wording(string id, string state) =>
        Assert.False(string.IsNullOrEmpty(Strings.ResourceManager.GetString($"Check_{id}_{state}", Strings.Culture)), $"Check_{id}_{state}");

    [Fact]
    public void Details_go_into_the_sentence()
    {
        Assert.Equal("דפדפנים נתמכים במחשב: Edge, Chrome.", Bidi.Plain(CheckItem.From(new CheckResult("browsers", "ok", "edge,chrome")).Text));
        Assert.StartsWith("הרשת Network 3 מוגדרת כרשת ציבורית", Bidi.Plain(CheckItem.From(new CheckResult("network", "warn", "Network 3")).Text));
        Assert.Equal("חומת האש מאפשרת למחשבי הילדים להתחבר (פורט 47800).", Bidi.Plain(CheckItem.From(new CheckResult("hub_port", "ok", "47800")).Text));
    }

    [Fact]
    public void The_onedrive_check_says_whether_children_can_use_it_and_how_to_fix_it()
    {
        // PROTECTION-SPEC §2: only a OneDrive installed for all users is allowed for restricted accounts.
        var machineWide = CheckItem.From(new CheckResult("onedrive", "ok"));
        Assert.True(machineWide.IsOk);
        Assert.StartsWith("OneDrive מותקן לכל המשתמשים", machineWide.Text, StringComparison.Ordinal);

        var none = CheckItem.From(new CheckResult("onedrive", "ok", "none"));
        Assert.Equal(("✓", "OneDrive לא מותקן."), (none.Glyph, none.Text));

        var perUser = CheckItem.From(new CheckResult("onedrive", "warn", "OneDriveSetup.exe /allusers"));
        Assert.Equal("!", perUser.Glyph);
        Assert.Contains("התקינו אותו מחדש לכל המשתמשים", perUser.Text, StringComparison.Ordinal);
        // The command on a line of its own, so it is not broken at its '/'.
        Assert.Equal("OneDriveSetup.exe /allusers", Bidi.Plain(perUser.Text)!.Split('\n')[^1]);
    }

    [Fact]
    public async Task The_settings_page_shows_the_onedrive_check()
    {
        _client.Checks.Add(new CheckResult("onedrive", "ok", "none"));
        var vm = new SettingsViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));

        await vm.RefreshAsync();

        Assert.Contains(vm.Checks, c => c.Text == "OneDrive לא מותקן.");
    }

    [Theory]
    [InlineData("ok", "✓", true, false)]
    [InlineData("warn", "!", false, false)]
    [InlineData("fail", "✕", false, true)]
    public void The_mark_follows_the_state(string state, string glyph, bool ok, bool fail)
    {
        var item = new CheckItem(state, "text");
        Assert.Equal((glyph, ok, fail), (item.Glyph, item.IsOk, item.IsFail));
    }

    [Fact]
    public void A_check_from_a_newer_service_is_shown_by_its_codes()
    {
        Assert.Equal("wfp: fail native engine missing", CheckItem.From(new CheckResult("wfp", "fail", "native engine missing")).Text);
    }

    [Fact]
    public async Task The_wizard_shows_the_checks_on_its_summary()
    {
        _client.Settings = SetupWizardViewModelTests.EmptyFamily();
        _client.Status = TestData.Status(1) with { Members = [] };
        _client.Checks.Add(new CheckResult("network", "warn", "Network 3"));
        var vm = new SetupWizardViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));
        await vm.RefreshAsync();
        vm.Members[0].Name = "נועה";

        for (var i = 0; i < 4; i++) await vm.NextCommand.ExecuteAsync(null);

        Assert.True(vm.IsReviewStep);
        Assert.True(vm.HasChecks);
        Assert.Equal(6, vm.Checks.Count);
        Assert.Contains(vm.Checks, c => c.State == "warn" && Bidi.Plain(c.Text)!.Contains("Network 3", StringComparison.Ordinal));
        Assert.Single(_client.SentOf<GetDiagnostics>());
    }

    [Fact]
    public async Task A_service_without_the_checks_just_shows_none()
    {
        _client.Settings = SetupWizardViewModelTests.EmptyFamily();
        _client.Status = TestData.Status(1) with { Members = [] };
        _client.Reply = m => m is GetDiagnostics ? new AdminError("unsupported", "Unsupported message type GetDiagnostics.") : null;
        var vm = new SetupWizardViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));
        await vm.RefreshAsync();
        vm.Members[0].Name = "נועה";

        for (var i = 0; i < 4; i++) await vm.NextCommand.ExecuteAsync(null);

        Assert.True(vm.IsReviewStep);
        Assert.False(vm.HasChecks);
        Assert.Null(vm.ErrorMessage);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_settings_page_shows_the_checks_on_the_hub_and_on_an_agent(bool isHub)
    {
        _client.IsHub = isHub;
        var vm = new SettingsViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));

        await vm.RefreshAsync();

        Assert.True(vm.HasChecks);
        Assert.Equal(5, vm.Checks.Count);
        Assert.Equal("✓", vm.Checks.Single(c => Bidi.Plain(c.Text)!.StartsWith("דפדפנים", StringComparison.Ordinal)).Glyph);
    }
}

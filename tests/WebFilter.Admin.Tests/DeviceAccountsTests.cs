using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.Tests;

/// <summary>Linking each computer's Windows accounts to family members on the devices page.</summary>
public sealed class DeviceAccountsTests
{
    private readonly FakeAdminClient _client = new();
    private readonly ManualTimer _countdown = new();

    public DeviceAccountsTests() => _client.Devices.AddRange(TestData.Devices());

    private async Task<DevicesViewModel> LoadedAsync()
    {
        var vm = new DevicesViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)), new FakeDialogs(), new FakeClipboard(), _countdown);
        await vm.RefreshAsync();
        return vm;
    }

    private static DeviceAccountItemViewModel Account(DevicesViewModel vm, string device, string name) =>
        vm.AccountGroups.Single(g => g.DeviceId == device).Accounts.Single(a => a.Name == name);

    [Fact]
    public async Task This_computer_comes_first_then_the_paired_ones_with_their_accounts()
    {
        var vm = await LoadedAsync();

        // laptop-yoav is in the settings but not paired here (TestData.Devices), so it is not listed.
        Assert.Equal(["המחשב הזה (pc-parents)", "KIDS-PC"], vm.AccountGroups.Select(g => Bidi.Plain(g.Title)));
        Assert.True(vm.AccountGroups[0].IsThisComputer);
        Assert.Equal(["noa", "savta", "visitor", "yoav", "dana"], vm.AccountGroups[1].Accounts.Select(a => a.Name));
        Assert.Equal(["noa", "savta", SetupWizardViewModel.NotLinked, "yoav", "dana"], vm.AccountGroups[1].Accounts.Select(a => a.MemberId));
        Assert.Equal(["לא מקושר", "נועה", "יואב", "דנה", "סבתא"], vm.LinkOptions.Select(o => o.Label));
        Assert.Equal(["לא צוין", "דנה", "סבתא"], vm.AdminLinkOptions.Select(o => o.Label));

        // A linked standard account: its member, to change. The one nobody answered for: the new-account card instead.
        var noa = Account(vm, "pc-kids", "noa");
        Assert.Equal((true, false, false, null), (noa.ShowsChoice, noa.ShowsCard, noa.ShowsGuest, noa.Card));
        Assert.Same(vm.LinkOptions, noa.Choices);
        var visitor = Account(vm, "pc-kids", "visitor");
        Assert.Equal((false, true, false), (visitor.ShowsChoice, visitor.ShowsCard, visitor.ShowsGuest));
        Assert.Equal("נמצא חשבון Windows חדש: visitor (במחשב KIDS-PC)", Bidi.Plain(visitor.Card!.Title));
        Assert.Null(visitor.Warning);
        Assert.Equal("1", vm.Badge);
        Assert.Equal("חשבונות Windows חדשים שעדיין לא שויכו לבן משפחה", vm.BadgeToolTip);
    }

    [Fact]
    public async Task Choosing_another_member_for_a_linked_account_saves_at_once_and_only_that_account()
    {
        var vm = await LoadedAsync();

        Account(vm, "pc-kids", "savta").MemberId = "dana";

        var replace = _client.Single<ReplaceSettings>();
        Assert.Equal(7, replace.IfVersion);
        var kids = replace.Settings.Devices.Single(d => d.Id == "pc-kids").Accounts;
        Assert.Equal("dana", kids.Single(a => a.Name == "savta").Member);
        Assert.Equal(TestData.Family().Devices.Single(d => d.Id == "pc-kids").Accounts.Where(a => a.Name != "savta"), kids.Where(a => a.Name != "savta"));
        Assert.Equal("החשבון savta ב-KIDS-PC קושר לדנה.", Bidi.Plain(vm.StatusMessage));
        Assert.Equal("dana", Account(vm, "pc-kids", "savta").MemberId);
        Assert.Equal("1", vm.Badge);
    }

    [Fact]
    public async Task Unlinking_an_account_is_saved_and_brings_the_new_account_card()
    {
        var vm = await LoadedAsync();

        Account(vm, "pc-kids", "savta").MemberId = SetupWizardViewModel.NotLinked;

        Assert.Null(_client.Single<ReplaceSettings>().Settings.Devices.Single(d => d.Id == "pc-kids").Accounts.Single(a => a.Name == "savta").Member);
        Assert.Equal("הקישור של החשבון savta ב-KIDS-PC בוטל.", Bidi.Plain(vm.StatusMessage));
        var savta = Account(vm, "pc-kids", "savta");
        Assert.Equal((false, true), (savta.ShowsChoice, savta.ShowsCard));
        Assert.NotNull(savta.Card);
        Assert.Equal("2", vm.Badge);
    }

    [Fact]
    public async Task A_refused_link_goes_back_to_what_is_stored()
    {
        var vm = await LoadedAsync();
        _client.Reply = m => m is ReplaceSettings ? new AdminError("bad_request", "database is locked") : null;

        Account(vm, "pc-kids", "savta").MemberId = "noa";

        Assert.Equal("savta", Account(vm, "pc-kids", "savta").MemberId);
        Assert.Equal("שירות הסינון לא ביצע את הפעולה: database is locked", vm.ErrorMessage);
    }

    /// <summary>
    /// Administrators are never filtered: no children to choose. Linking one to an adult only says whose it is (the setup
    /// wizard does it for the parents' own accounts), so that stays possible — adults only.
    /// </summary>
    [Fact]
    public async Task An_administrator_account_offers_only_the_adults_and_is_never_a_new_account()
    {
        var vm = await LoadedAsync();
        var dana = Account(vm, "pc-kids", "dana");

        Assert.True(dana.IsAdmin);
        Assert.Same(vm.AdminLinkOptions, dana.Choices);
        Assert.Equal((true, false, false, null, null), (dana.ShowsChoice, dana.ShowsCard, dana.ShowsGuest, dana.Card, dana.Warning));

        dana.MemberId = "savta";

        Assert.Equal("savta", _client.Single<ReplaceSettings>().Settings.Devices.Single(d => d.Id == "pc-kids").Accounts.Single(a => a.Name == "dana").Member);
    }

    [Fact]
    public async Task An_administrator_account_with_no_adults_to_choose_shows_no_choice()
    {
        _client.Settings = _client.Settings with
        {
            Members = [.. _client.Settings.Members.Where(m => m.FilterMode != "unrestricted")],
            Devices = [.. _client.Settings.Devices.Select(d => d with { Accounts = [.. d.Accounts.Select(a => a.Member is "dana" or "savta" ? a with { Member = null } : a)] })],
        };
        var vm = await LoadedAsync();

        var dana = Account(vm, "pc-kids", "dana");
        Assert.Equal(["לא צוין"], vm.AdminLinkOptions.Select(o => o.Label));
        Assert.Equal((false, false, null), (dana.ShowsChoice, dana.ShowsCard, dana.Card));
        // savta's standard account, now unlinked, is a new account; the administrator never is.
        Assert.Equal("2", vm.Badge);
    }

    [Fact]
    public async Task An_administrator_account_linked_to_a_filtered_member_is_warned_about_and_can_be_unlinked()
    {
        _client.Settings = _client.Settings with
        {
            Devices = [.. _client.Settings.Devices.Select(d => d.Id != "pc-kids" ? d : d with { Accounts = [.. d.Accounts.Select(a => a.Name == "dana" ? a with { Member = "noa" } : a)] })],
        };
        var vm = await LoadedAsync();
        var dana = Account(vm, "pc-kids", "dana");

        Assert.StartsWith("dana הוא חשבון מנהל", dana.Warning);
        Assert.Contains("תנו ל-נועה חשבון Windows רגיל", dana.Warning);
        Assert.False(dana.ShowsChoice);

        dana.UnlinkCommand.Execute(null);

        Assert.Null(_client.Single<ReplaceSettings>().Settings.Devices.Single(d => d.Id == "pc-kids").Accounts.Single(a => a.Name == "dana").Member);
        Assert.Equal("הקישור של החשבון dana ב-KIDS-PC בוטל.", Bidi.Plain(vm.StatusMessage));
        Assert.Null(dana.Warning);
        Assert.True(dana.ShowsChoice);
        Assert.Same(vm.AdminLinkOptions, dana.Choices);
        Assert.Null(dana.Card);
        Assert.Equal("1", vm.Badge);
    }

    [Fact]
    public async Task Refreshing_never_saves_anything()
    {
        var vm = await LoadedAsync();
        _client.BumpVersion(_client.Settings with { Members = [.. _client.Settings.Members.Select(m => m.Id == "noa" ? m with { DisplayName = "נועי" } : m)] });

        await vm.RefreshAsync();
        await vm.RefreshAsync();

        Assert.Empty(_client.SentOf<ReplaceSettings>());
        Assert.Equal("noa", Account(vm, "pc-kids", "noa").MemberId);
        Assert.Contains(vm.LinkOptions, o => o.Label == "נועי");
    }

    [Fact]
    public async Task An_account_reported_meanwhile_appears_on_the_next_refresh()
    {
        var vm = await LoadedAsync();
        var kids = _client.Settings.Devices.Single(d => d.Id == "pc-kids");
        _client.BumpVersion(_client.Settings with
        {
            Devices = [.. _client.Settings.Devices.Select(d => d.Id == "pc-kids" ? d with { Accounts = [.. kids.Accounts, new AccountDto("S-1-5-21-2000-1009", "cousin", null, false)] } : d)],
        });

        await vm.RefreshAsync();

        Assert.Contains(vm.AccountGroups[1].Accounts, a => a.Name == "cousin" && a.MemberId == SetupWizardViewModel.NotLinked);
        Assert.Equal("2", vm.Badge);
    }

    [Theory]
    [InlineData(0, "לא נמצאו חשבונות חדשים במחשב הזה.")]
    [InlineData(1, "נמצא חשבון חדש אחד במחשב הזה.")]
    [InlineData(2, "נמצאו 2 חשבונות חדשים במחשב הזה.")]
    public async Task Looking_for_new_accounts_on_this_computer_says_what_it_found(int found, string message)
    {
        _client.LocalAccounts.AddRange(Enumerable.Range(0, found).Select(i => new AccountDto($"S-1-5-21-1000-20{i}", $"kid{i}", null, false)));
        var vm = await LoadedAsync();

        await vm.ScanAccountsCommand.ExecuteAsync(null);

        Assert.Equal(message, vm.StatusMessage);
        Assert.Equal(found + 1, vm.AccountGroups[0].Accounts.Count);
    }
}

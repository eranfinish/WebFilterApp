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
        Assert.Equal("לא מקושר — הגלישה בחשבון הזה חסומה כמעט לגמרי.", Account(vm, "pc-kids", "visitor").Warning);
        Assert.Null(Account(vm, "pc-kids", "dana").Warning);
        Assert.True(Account(vm, "pc-kids", "dana").IsAdmin);
        Assert.Equal("1", vm.Badge);
    }

    [Fact]
    public async Task Choosing_a_member_links_the_account_at_once_and_only_that_account()
    {
        var vm = await LoadedAsync();

        Account(vm, "pc-kids", "visitor").MemberId = "yoav";

        var replace = _client.Single<ReplaceSettings>();
        Assert.Equal(7, replace.IfVersion);
        var kids = replace.Settings.Devices.Single(d => d.Id == "pc-kids").Accounts;
        Assert.Equal("yoav", kids.Single(a => a.Name == "visitor").Member);
        Assert.Equal(TestData.Family().Devices.Single(d => d.Id == "pc-kids").Accounts.Where(a => a.Name != "visitor"), kids.Where(a => a.Name != "visitor"));
        Assert.Equal("החשבון visitor ב-KIDS-PC קושר ליואב.", Bidi.Plain(vm.StatusMessage));
        Assert.Equal("yoav", Account(vm, "pc-kids", "visitor").MemberId);
        Assert.Null(Account(vm, "pc-kids", "visitor").Warning);
        Assert.Null(vm.Badge);
    }

    [Fact]
    public async Task Unlinking_an_account_is_saved_too()
    {
        var vm = await LoadedAsync();

        Account(vm, "pc-kids", "savta").MemberId = SetupWizardViewModel.NotLinked;

        Assert.Null(_client.Single<ReplaceSettings>().Settings.Devices.Single(d => d.Id == "pc-kids").Accounts.Single(a => a.Name == "savta").Member);
        Assert.Equal("הקישור של החשבון savta ב-KIDS-PC בוטל.", Bidi.Plain(vm.StatusMessage));
    }

    [Fact]
    public async Task An_administrator_account_linked_to_a_filtered_member_is_warned_about()
    {
        var vm = await LoadedAsync();

        Account(vm, "pc-kids", "dana").MemberId = "noa";

        Assert.StartsWith("dana הוא חשבון מנהל", Account(vm, "pc-kids", "dana").Warning);
    }

    [Fact]
    public async Task A_refused_link_goes_back_to_what_is_stored()
    {
        var vm = await LoadedAsync();
        _client.Reply = m => m is ReplaceSettings ? new AdminError("bad_request", "database is locked") : null;

        Account(vm, "pc-kids", "visitor").MemberId = "noa";

        Assert.Equal(SetupWizardViewModel.NotLinked, Account(vm, "pc-kids", "visitor").MemberId);
        Assert.Equal("שירות הסינון לא ביצע את הפעולה: database is locked", vm.ErrorMessage);
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

    [Fact]
    public async Task The_dashboard_points_to_unlinked_accounts()
    {
        var dashboard = new DashboardViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));

        await dashboard.RefreshAsync();

        Assert.True(dashboard.HasUnlinkedAccounts);
        Assert.Equal("חשבון Windows אחד עוד לא קושר לבן משפחה, והגלישה בו חסומה כמעט לגמרי.", dashboard.UnlinkedAccountsText);
    }
}

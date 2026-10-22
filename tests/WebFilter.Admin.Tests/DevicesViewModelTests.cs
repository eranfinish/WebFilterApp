using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Tests;

public sealed class DevicesViewModelTests
{
    private const string FirstCommand = "WebFilter.Service.exe pair --hub 192.168.1.20:47800 --code K7QM-2RXD-9HBA";

    private readonly FakeAdminClient _client = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly ManualTimer _countdown = new();
    private readonly ManualTime _time = new(TestData.Now);

    public DevicesViewModelTests() => _client.Devices.AddRange(TestData.Devices());

    private async Task<DevicesViewModel> LoadedAsync()
    {
        var vm = new DevicesViewModel(new FamilyContext(_client, _time), _dialogs, _clipboard, _countdown);
        await vm.RefreshAsync();
        return vm;
    }

    private async Task<PairingViewModel> PairingAsync(DevicesViewModel vm)
    {
        await vm.StartPairingCommand.ExecuteAsync(null);
        return Assert.IsType<PairingViewModel>(vm.Pairing);
    }

    /// <summary>Moves this PC's clock and the service's together.</summary>
    private void Advance(TimeSpan by)
    {
        _time.Now += by;
        _client.Status = _client.Status with { Now = _client.Status.Now + by };
    }

    private Task TickCountdownAsync() => _countdown.FireAsync(PairingViewModel.CountdownInterval);

    [Fact]
    public async Task Lists_active_computers_by_name_and_revoked_ones_apart()
    {
        var vm = await LoadedAsync();

        Assert.True(vm.IsHub);
        Assert.False(vm.IsNotHub);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(["pc-basement", "pc-kids", "pc-yoav"], vm.Active.Select(d => d.Id));
        Assert.Equal(["pc-old"], vm.Revoked.Select(d => d.Id));
        Assert.False(vm.HasNoActive);
        Assert.True(vm.HasRevoked);
        // Devices first (an agent answers not_hub there), then the status for the clock and the time zone.
        Assert.IsType<GetDevices>(_client.Sent[0]);
        Assert.IsType<GetStatus>(_client.Sent[1]);

        var kids = vm.Active[1];
        Assert.Equal("KIDS-PC", kids.Name);
        Assert.Equal("מזהה: pc-kids", kids.IdText);
        Assert.Equal("Windows", kids.OsText);
        Assert.Equal("01.12.2025", kids.PairedText);
        Assert.Equal("לפני פחות מדקה", kids.LastSeenText);
        Assert.True(kids.IsOnline);
        Assert.Equal("פעיל", kids.StatusText);
        Assert.False(kids.IsRevoked);

        var yoav = vm.Active[2];
        Assert.Equal("07.10.2026", yoav.PairedText);
        Assert.Equal("לפני 42 דקות", yoav.LastSeenText);
        Assert.False(yoav.IsOnline);

        Assert.Equal("עדיין לא התחבר", vm.Active[0].LastSeenText);
        Assert.False(vm.Active[0].IsOnline);

        var old = vm.Revoked[0];
        Assert.True(old.IsRevoked);
        Assert.Equal("הצימוד בוטל", old.StatusText);
        Assert.Equal("19.09 16:00", old.LastSeenText);
        Assert.False(old.IsOnline);
    }

    [Fact]
    public async Task A_computer_that_unregistered_itself_on_uninstall_is_listed_with_the_revoked_ones_as_uninstalled()
    {
        var index = _client.Devices.FindIndex(d => d.Id == "pc-yoav");
        _client.Devices[index] = _client.Devices[index] with { Revoked = true, RevokedReason = Contracts.Hub.UnregisterRequest.Uninstall };

        var vm = await LoadedAsync();

        var yoav = vm.Revoked.Single(d => d.Id == "pc-yoav");
        Assert.True(yoav.IsRevoked);
        Assert.False(yoav.IsOnline);
        Assert.Equal("הסינון הוסר", yoav.StatusText);
        Assert.Equal("הצימוד בוטל", vm.Revoked.Single(d => d.Id == "pc-old").StatusText);
    }

    [Theory]
    [InlineData(-5, "לפני פחות מדקה", true)] // the service's clock slightly behind the device's
    [InlineData(59, "לפני פחות מדקה", true)]
    [InlineData(60, "לפני דקה", true)]
    [InlineData(119, "לפני דקה", true)]
    [InlineData(120, "לפני 2 דקות", false)]
    [InlineData(59 * 60 + 59, "לפני 59 דקות", false)]
    [InlineData(60 * 60, "לפני שעה", false)]
    [InlineData(2 * 60 * 60, "לפני 2 שעות", false)]
    [InlineData(24 * 60 * 60 - 1, "לפני 23 שעות", false)]
    [InlineData(24 * 60 * 60, "18.10 16:00", false)]
    public async Task Last_seen_is_relative_for_a_day_then_a_date(int secondsAgo, string expected, bool online)
    {
        _client.Devices.Clear();
        _client.Devices.Add(new DeviceInfo("pc-kids", "KIDS-PC", "windows", TestData.Now.AddDays(-1), TestData.Now.AddSeconds(-secondsAgo), Revoked: false));

        var vm = await LoadedAsync();

        Assert.Equal(expected, vm.Active[0].LastSeenText);
        Assert.Equal(online, vm.Active[0].IsOnline);
    }

    [Fact]
    public async Task Unnamed_computers_show_their_id_and_unknown_systems_their_code()
    {
        _client.Devices.Clear();
        _client.Devices.Add(new DeviceInfo("pc-x", "  ", "chromeos", TestData.Now, null, Revoked: false));
        _client.Devices.Add(new DeviceInfo("tablet", "Tablet", "android", TestData.Now, null, Revoked: false));

        var vm = await LoadedAsync();

        Assert.Equal(["pc-x", "Tablet"], vm.Active.Select(d => d.Name));
        Assert.Equal(["chromeos", "Android"], vm.Active.Select(d => d.OsText));
    }

    [Fact]
    public async Task Without_paired_computers_the_page_says_how_to_add_one()
    {
        _client.Devices.Clear();

        var vm = await LoadedAsync();

        Assert.True(vm.IsHub);
        Assert.Empty(vm.Active);
        Assert.True(vm.HasNoActive);
        Assert.False(vm.HasRevoked);
        Assert.Contains("צימוד מחשב חדש", Strings.Devices_Empty);
    }

    [Fact]
    public async Task A_refresh_updates_rows_in_place()
    {
        var vm = await LoadedAsync();
        var kids = vm.Active.Single(d => d.Id == "pc-kids");

        Advance(TimeSpan.FromMinutes(5));
        _client.Devices.RemoveAll(d => d.Id == "pc-basement");
        _client.Devices.Add(new DeviceInfo("pc-attic", "ATTIC", "windows", TestData.Now, TestData.Now, Revoked: false));
        await vm.RefreshAsync();

        Assert.Equal(["pc-attic", "pc-kids", "pc-yoav"], vm.Active.Select(d => d.Id));
        Assert.Same(kids, vm.Active[1]);
        Assert.Equal("לפני 5 דקות", kids.LastSeenText);
        Assert.False(kids.IsOnline);
    }

    [Fact]
    public async Task Revoking_asks_first_and_explains_what_happens()
    {
        var vm = await LoadedAsync();
        _dialogs.Answer = false;

        await vm.RevokeCommand.ExecuteAsync(vm.Active[1]);

        Assert.Empty(_client.SentOf<RevokeDevice>());
        Assert.Equal("ביטול צימוד", Assert.Single(_dialogs.Titles));
        var question = Bidi.Plain(Assert.Single(_dialogs.Asked))!;
        Assert.StartsWith("לבטל את הצימוד של KIDS-PC?", question);
        Assert.Contains("ימשיך לאכוף את ההגדרות האחרונות שקיבל", question);
        Assert.Contains("יפסיק לקבל עדכונים", question);
        Assert.Contains("pc-kids", vm.Active.Select(d => d.Id));
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task Revoking_after_confirming_sends_RevokeDevice_and_lists_it_as_revoked()
    {
        var vm = await LoadedAsync();
        var devicesCalls = _client.SentOf<GetDevices>().Count();

        await vm.RevokeCommand.ExecuteAsync(vm.Active[1]);

        Assert.Equal(new RevokeDevice("pc-kids"), _client.Single<RevokeDevice>());
        Assert.Equal("הצימוד של KIDS-PC בוטל.", Bidi.Plain(vm.StatusMessage));
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(devicesCalls + 1, _client.SentOf<GetDevices>().Count());
        Assert.DoesNotContain("pc-kids", vm.Active.Select(d => d.Id));
        Assert.Equal(["pc-kids", "pc-old"], vm.Revoked.Select(d => d.Id));
        Assert.False(vm.Revoked[0].IsOnline);
    }

    [Fact]
    public async Task Revoked_computers_cannot_be_revoked_again()
    {
        var vm = await LoadedAsync();

        await vm.RevokeCommand.ExecuteAsync(vm.Revoked[0]);
        await vm.RevokeCommand.ExecuteAsync(null);

        Assert.Empty(_dialogs.Asked);
        Assert.Empty(_client.SentOf<RevokeDevice>());
    }

    [Fact]
    public async Task A_computer_revoked_elsewhere_is_reported_and_the_list_refreshed()
    {
        var vm = await LoadedAsync();
        // Another admin window revoked it after this page loaded: the service answers not_found.
        var index = _client.Devices.FindIndex(d => d.Id == "pc-yoav");
        _client.Devices[index] = _client.Devices[index] with { Revoked = true };

        await vm.RevokeCommand.ExecuteAsync(vm.Active.Single(d => d.Id == "pc-yoav"));

        Assert.Single(_client.SentOf<RevokeDevice>());
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("הצימוד של YOAV-LAPTOP כבר בוטל. הרשימה עודכנה.", Bidi.Plain(vm.StatusMessage));
        Assert.Contains("pc-yoav", vm.Revoked.Select(d => d.Id));
        Assert.DoesNotContain("pc-yoav", vm.Active.Select(d => d.Id));
    }

    [Fact]
    public async Task A_revoke_the_service_rejects_is_explained()
    {
        var vm = await LoadedAsync();
        _client.Reply = m => m is RevokeDevice ? new AdminError("bad_request", "database is locked") : null;

        await vm.RevokeCommand.ExecuteAsync(vm.Active[1]);

        Assert.Equal("שירות הסינון לא ביצע את הפעולה: database is locked", vm.ErrorMessage);
        Assert.Null(vm.StatusMessage);
        Assert.Contains("pc-kids", vm.Active.Select(d => d.Id));
    }

    [Fact]
    public async Task On_an_agent_computer_the_page_says_devices_are_managed_on_the_hub()
    {
        _client.IsHub = false;

        var vm = await LoadedAsync();

        Assert.True(vm.IsNotHub);
        Assert.False(vm.IsHub);
        Assert.True(vm.HasLoaded);
        Assert.Null(vm.ErrorMessage);
        Assert.Empty(vm.Active);
        Assert.Empty(vm.Revoked);
        Assert.False(vm.HasNoActive);
        Assert.False(vm.HasRevoked);
        // Devices first: the agent's not_hub settles it, the status is not needed.
        Assert.Empty(_client.SentOf<GetStatus>());
        Assert.Contains("מחשב ההורים", Strings.Devices_NotHub);
    }

    [Fact]
    public async Task A_hub_that_became_an_agent_closes_the_pairing_panel()
    {
        var vm = await LoadedAsync();
        await PairingAsync(vm);

        _client.IsHub = false;
        await vm.RefreshAsync();

        Assert.True(vm.IsNotHub);
        Assert.False(vm.IsHub);
        Assert.Null(vm.Pairing);
        Assert.False(vm.IsPairing);
        Assert.Empty(vm.Active);
        Assert.Empty(_countdown.Running);
    }

    [Fact]
    public async Task Asking_an_agent_for_a_code_shows_the_not_hub_message()
    {
        var vm = await LoadedAsync();
        _client.IsHub = false;

        await vm.StartPairingCommand.ExecuteAsync(null);

        Assert.True(vm.IsNotHub);
        Assert.Null(vm.Pairing);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Pairing_shows_the_code_the_addresses_the_port_and_the_command()
    {
        var vm = await LoadedAsync();
        Assert.False(vm.IsPairing);

        var pairing = await PairingAsync(vm);

        Assert.Single(_client.SentOf<CreatePairingCode>());
        Assert.True(vm.IsPairing);
        Assert.Equal("K7QM-2RXD-9HBA", pairing.Code);
        Assert.Equal(TestData.HubAddresses, pairing.Addresses);
        Assert.Equal("192.168.1.20", pairing.SelectedAddress);
        Assert.True(pairing.HasAddresses);
        Assert.True(pairing.HasSeveralAddresses);
        Assert.Equal(47800, pairing.Port);
        Assert.Equal("47800", pairing.PortText);
        Assert.Equal(FirstCommand, pairing.PairCommandText);
        Assert.Equal("הקוד בתוקף עוד 10:00", pairing.CountdownText);
        Assert.False(pairing.IsExpired);
        Assert.True(pairing.CopyCommand.CanExecute(null));
        Assert.Equal([PairingViewModel.CountdownInterval], _countdown.Running);
    }

    [Theory]
    [InlineData("10.0.0.5", 47800, "ABCD-EFGH-JKMN", "WebFilter.Service.exe pair --hub 10.0.0.5:47800 --code ABCD-EFGH-JKMN")]
    [InlineData("192.168.1.20", 8443, "K7QM-2RXD-9HBA", "WebFilter.Service.exe pair --hub 192.168.1.20:8443 --code K7QM-2RXD-9HBA")]
    public void The_command_is_the_agents_pair_command(string address, int port, string code, string expected) =>
        Assert.Equal(expected, PairingViewModel.BuildCommand(address, port, code));

    [Fact]
    public async Task Picking_another_address_changes_the_command()
    {
        var vm = await LoadedAsync();
        var pairing = await PairingAsync(vm);
        pairing.CopyCommand.Execute(null);

        pairing.SelectedAddress = "172.24.48.1";

        Assert.Equal("WebFilter.Service.exe pair --hub 172.24.48.1:47800 --code K7QM-2RXD-9HBA", pairing.PairCommandText);
        Assert.Null(pairing.CopyMessage);
    }

    [Fact]
    public async Task Copy_puts_the_command_on_the_clipboard()
    {
        var vm = await LoadedAsync();
        var pairing = await PairingAsync(vm);

        pairing.CopyCommand.Execute(null);

        Assert.Equal(FirstCommand, _clipboard.Text);
        Assert.Equal("הפקודה הועתקה ללוח.", pairing.CopyMessage);
        Assert.False(pairing.CopyFailed);
    }

    [Fact]
    public async Task A_busy_clipboard_asks_to_copy_by_hand()
    {
        var vm = await LoadedAsync();
        var pairing = await PairingAsync(vm);
        _clipboard.Available = false;

        pairing.CopyCommand.Execute(null);

        Assert.Null(_clipboard.Text);
        Assert.True(pairing.CopyFailed);
        Assert.Contains("Ctrl+C", pairing.CopyMessage);
    }

    [Fact]
    public async Task Without_a_network_address_there_is_no_command_to_copy()
    {
        _client.PairingAddresses.Clear();
        var vm = await LoadedAsync();

        var pairing = await PairingAsync(vm);

        Assert.False(pairing.HasAddresses);
        Assert.False(pairing.HasSeveralAddresses);
        Assert.Null(pairing.SelectedAddress);
        Assert.Null(pairing.PairCommandText);
        Assert.False(pairing.CopyCommand.CanExecute(null));
        Assert.Equal("K7QM-2RXD-9HBA", pairing.Code);
    }

    [Fact]
    public async Task The_countdown_follows_the_clock_and_the_code_expires()
    {
        var vm = await LoadedAsync();
        var pairing = await PairingAsync(vm);

        Advance(TimeSpan.FromSeconds(61));
        await TickCountdownAsync();
        Assert.Equal("הקוד בתוקף עוד 8:59", pairing.CountdownText);

        Advance(TimeSpan.FromSeconds(9 * 60 - 1.5));
        await TickCountdownAsync();
        Assert.Equal("הקוד בתוקף עוד 0:01", pairing.CountdownText);
        Assert.False(pairing.IsExpired);

        Advance(TimeSpan.FromSeconds(0.5));
        await TickCountdownAsync();

        Assert.True(pairing.IsExpired);
        Assert.Equal(Strings.Pairing_Expired, pairing.CountdownText);
        Assert.False(pairing.CopyCommand.CanExecute(null));
        Assert.Empty(_countdown.Running);
        Assert.Same(pairing, vm.Pairing);
    }

    [Fact]
    public async Task An_expired_code_can_be_replaced_with_a_new_one()
    {
        var vm = await LoadedAsync();
        var pairing = await PairingAsync(vm);
        pairing.SelectedAddress = "172.24.48.1";
        Advance(TimeSpan.FromMinutes(10));
        await TickCountdownAsync();
        Assert.True(pairing.IsExpired);

        _client.PairingCode = "WXYZ-2345-6789";
        await pairing.NewCodeCommand.ExecuteAsync(null);

        Assert.Equal(2, _client.SentOf<CreatePairingCode>().Count());
        Assert.Same(pairing, vm.Pairing);
        Assert.False(pairing.IsExpired);
        Assert.Equal("WXYZ-2345-6789", pairing.Code);
        Assert.Equal("הקוד בתוקף עוד 10:00", pairing.CountdownText);
        // The address the parent picked stays picked.
        Assert.Equal("WebFilter.Service.exe pair --hub 172.24.48.1:47800 --code WXYZ-2345-6789", pairing.PairCommandText);
        Assert.True(pairing.CopyCommand.CanExecute(null));
        Assert.Equal([PairingViewModel.CountdownInterval], _countdown.Running);
    }

    [Fact]
    public async Task A_code_that_arrives_already_expired_is_shown_expired()
    {
        var vm = await LoadedAsync();
        _client.Reply = m => m is CreatePairingCode ? new PairingCodeResult("K7QM-2RXD-9HBA", TestData.Now.AddSeconds(-1), 47800, ["192.168.1.20"]) : null;

        var pairing = await PairingAsync(vm);

        Assert.True(pairing.IsExpired);
        Assert.Empty(_countdown.Running);
    }

    [Fact]
    public async Task A_refresh_keeps_the_open_pairing_panel()
    {
        var vm = await LoadedAsync();
        var pairing = await PairingAsync(vm);
        pairing.SelectedAddress = "172.24.48.1";
        pairing.CopyCommand.Execute(null);

        Advance(TimeSpan.FromSeconds(30));
        _client.Devices.Add(new DeviceInfo("pc-new", "NEW-PC", "windows", TestData.Now.AddSeconds(25), TestData.Now.AddSeconds(25), Revoked: false));
        await vm.RefreshAsync();
        await TickCountdownAsync();

        Assert.Contains("pc-new", vm.Active.Select(d => d.Id));
        Assert.Same(pairing, vm.Pairing);
        Assert.Equal("K7QM-2RXD-9HBA", pairing.Code);
        Assert.Equal("172.24.48.1", pairing.SelectedAddress);
        Assert.Equal("הפקודה הועתקה ללוח.", pairing.CopyMessage);
        Assert.Equal("הקוד בתוקף עוד 9:30", pairing.CountdownText);
        Assert.Single(_client.SentOf<CreatePairingCode>());
        Assert.Equal([PairingViewModel.CountdownInterval], _countdown.Running);
    }

    [Fact]
    public async Task Closing_the_panel_stops_the_countdown_and_reopening_creates_a_new_code()
    {
        var vm = await LoadedAsync();
        await PairingAsync(vm);

        vm.ClosePairingCommand.Execute(null);

        Assert.Null(vm.Pairing);
        Assert.False(vm.IsPairing);
        Assert.Empty(_countdown.Running);

        var again = await PairingAsync(vm);
        Assert.Equal(2, _client.SentOf<CreatePairingCode>().Count());
        Assert.Equal("הקוד בתוקף עוד 10:00", again.CountdownText);
    }

    [Fact]
    public async Task The_panel_closes_itself_through_its_own_button()
    {
        var vm = await LoadedAsync();
        var pairing = await PairingAsync(vm);

        pairing.CloseCommand.Execute(null);

        Assert.Null(vm.Pairing);
        Assert.Empty(_countdown.Running);
    }

    [Fact]
    public async Task A_code_the_service_refuses_is_explained_and_no_panel_opens()
    {
        var vm = await LoadedAsync();
        _client.Reply = m => m is CreatePairingCode ? new AdminError("bad_request", "hub certificate missing") : null;

        await vm.StartPairingCommand.ExecuteAsync(null);

        Assert.Equal("שירות הסינון לא ביצע את הפעולה: hub certificate missing", vm.ErrorMessage);
        Assert.Null(vm.Pairing);
        Assert.Empty(_countdown.Running);
    }

    [Fact]
    public async Task A_failed_new_code_keeps_the_expired_one_on_screen()
    {
        var vm = await LoadedAsync();
        var pairing = await PairingAsync(vm);
        Advance(TimeSpan.FromMinutes(10));
        await TickCountdownAsync();
        _client.Failure = new AdminConnectionException(AdminConnectionError.Timeout, "no reply");

        await pairing.NewCodeCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Error_Timeout, vm.ErrorMessage);
        Assert.Same(pairing, vm.Pairing);
        Assert.True(pairing.IsExpired);
        Assert.Equal("K7QM-2RXD-9HBA", pairing.Code);
    }

    [Fact]
    public async Task When_the_service_is_not_running_the_banner_explains_and_the_page_shows_nothing()
    {
        _client.Failure = new AdminConnectionException(AdminConnectionError.ServiceNotRunning, "no pipe");

        var vm = await LoadedAsync();

        // Connection problems are the window banner's to explain.
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.HasLoaded);
        Assert.False(vm.IsHub);
        Assert.False(vm.IsNotHub);
        Assert.Empty(vm.Active);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_list()
    {
        var vm = await LoadedAsync();
        _client.Reply = m => m is GetDevices ? new AdminError("bad_request", "database is locked") : null;

        await vm.RefreshAsync();

        Assert.Equal("שירות הסינון לא ביצע את הפעולה: database is locked", vm.ErrorMessage);
        Assert.True(vm.IsHub);
        Assert.Equal(3, vm.Active.Count);
    }

    [Fact]
    public async Task An_unexpected_reply_is_reported()
    {
        _client.Reply = m => m is GetDevices ? new AdminOk() : null;

        var vm = await LoadedAsync();

        Assert.Equal(Strings.Error_UnexpectedReply, vm.ErrorMessage);
        Assert.False(vm.IsHub);
    }

    [Fact]
    public async Task Is_the_devices_page_and_refreshes_with_the_main_window()
    {
        var vm = await LoadedAsync();

        Assert.True(vm.AutoRefresh);
        Assert.Equal("מכשירים", vm.Title);
        Assert.False(string.IsNullOrWhiteSpace(vm.Intro));
    }
}

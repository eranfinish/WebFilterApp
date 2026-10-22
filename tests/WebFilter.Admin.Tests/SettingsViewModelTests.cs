using WebFilter.Admin.Services;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.Tests;

public sealed class SettingsViewModelTests
{
    private readonly FakeAdminClient _client = new();

    private async Task<SettingsViewModel> LoadedAsync()
    {
        var vm = new SettingsViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));
        await vm.RefreshAsync();
        return vm;
    }

    [Fact]
    public async Task Shows_members_and_connection_diagnostics()
    {
        var vm = await LoadedAsync();

        Assert.Equal(["noa", "yoav", "dana", "savta"], vm.Members.Select(m => m.Id));
        var noa = vm.Members[0];
        Assert.Equal("נועה", noa.DisplayName);
        Assert.Equal("ילד/ה", noa.RoleText);
        Assert.Equal("8", noa.AgeText);
        Assert.Equal("allowlist", noa.FilterMode);
        Assert.Equal("allowlist", noa.YouTubeMode);
        Assert.False(noa.IsDirty);
        Assert.Equal("מבוגר/ת", vm.Members[2].RoleText);
        Assert.Equal(["ללא סינון", "חסימת אתרים לא מתאימים", "רק אתרים מאושרים"], noa.FilterModeOptions.Select(o => o.Label));
        Assert.Equal(["off", "restricted", "allowlist", "unrestricted"], noa.YouTubeModeOptions.Select(o => o.Value));

        Assert.Equal("WebFilter.Test.Admin", vm.PipeName);
        Assert.Equal("מחובר לשירות הסינון", vm.ConnectionStateText);
        Assert.Equal("7", vm.SettingsVersionText);
        Assert.Equal("pc-parents", vm.DeviceText);
        Assert.Equal("כן", vm.IsHubText);
        Assert.Equal("19.10 16:00", vm.ServiceTimeText);
        Assert.Equal("Asia/Jerusalem", vm.TimeZoneText);
    }

    [Fact]
    public async Task Saving_sends_only_the_changed_fields()
    {
        var vm = await LoadedAsync();
        var yoav = vm.Members[1];
        Assert.False(yoav.SaveCommand.CanExecute(null));

        yoav.FilterMode = "allowlist";
        Assert.True(yoav.IsDirty);
        Assert.True(yoav.SaveCommand.CanExecute(null));
        await yoav.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new SetMember("yoav", FilterMode: "allowlist"), _client.Single<SetMember>());
        Assert.Equal("ההגדרות של יואב נשמרו.", vm.StatusMessage);
    }

    [Fact]
    public async Task Saving_both_modes_sends_both()
    {
        var vm = await LoadedAsync();
        var noa = vm.Members[0];
        noa.FilterMode = "blocklist";
        noa.YouTubeMode = "off";

        await noa.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new SetMember("noa", FilterMode: "blocklist", YouTubeMode: "off"), _client.Single<SetMember>());
    }

    [Fact]
    public async Task Reverting_restores_the_stored_modes()
    {
        var vm = await LoadedAsync();
        var noa = vm.Members[0];
        noa.YouTubeMode = "unrestricted";

        noa.RevertCommand.Execute(null);

        Assert.Equal("allowlist", noa.YouTubeMode);
        Assert.False(noa.IsDirty);
        Assert.Empty(_client.SentOf<SetMember>());
    }

    // ---- Windows features for children (PROTECTION-SPEC §2) ------------------------------------------------------

    [Fact]
    public async Task A_family_without_the_section_shows_the_defaults()
    {
        var vm = await LoadedAsync();

        var features = vm.WindowsFeatures!;
        Assert.Null(_client.Settings.WindowsFeatures);
        Assert.Equal((true, true, false), (features.NotificationsAndSync, features.OneDrive, features.WebSearchInStart));
        Assert.False(features.IsDirty);
        Assert.False(features.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task The_switches_are_saved_together_and_replace_only_that_part()
    {
        var vm = await LoadedAsync();
        var features = vm.WindowsFeatures!;
        features.OneDrive = false;
        features.WebSearchInStart = true;
        Assert.True(features.IsDirty);

        await features.SaveCommand.ExecuteAsync(null);

        var replace = _client.Single<ReplaceSettings>();
        Assert.Equal(7, replace.IfVersion);
        Assert.Equal(new WindowsFeaturesDto(NotificationsAndSync: true, OneDrive: false, WebSearchInStart: true), replace.Settings.WindowsFeatures);
        // Nothing else changes.
        Assert.Equal(FamilyBundleJson.Serialize(TestData.Family() with { WindowsFeatures = replace.Settings.WindowsFeatures }),
            FamilyBundleJson.Serialize(replace.Settings with { Version = 7 }));
        Assert.Equal("תכונות Windows לילדים נשמרו.", vm.StatusMessage);
        Assert.False(vm.WindowsFeatures!.IsDirty);
        Assert.Equal((true, false, true), (vm.WindowsFeatures.NotificationsAndSync, vm.WindowsFeatures.OneDrive, vm.WindowsFeatures.WebSearchInStart));
        Assert.Empty(_client.SentOf<SetMember>());
    }

    [Fact]
    public async Task Stored_switches_are_shown_and_can_be_reverted()
    {
        _client.Settings = _client.Settings with { WindowsFeatures = new WindowsFeaturesDto(NotificationsAndSync: false, OneDrive: true, WebSearchInStart: true) };
        var vm = await LoadedAsync();
        var features = vm.WindowsFeatures!;
        Assert.Equal((false, true, true), (features.NotificationsAndSync, features.OneDrive, features.WebSearchInStart));

        features.NotificationsAndSync = true;
        features.RevertCommand.Execute(null);

        Assert.False(features.NotificationsAndSync);
        Assert.False(features.IsDirty);
        Assert.Empty(_client.SentOf<ReplaceSettings>());
    }

    [Fact]
    public async Task Switches_being_changed_survive_a_reload()
    {
        var vm = await LoadedAsync();
        var features = vm.WindowsFeatures!;
        features.OneDrive = false;

        _client.BumpVersion();
        await vm.RefreshAsync();

        Assert.Same(features, vm.WindowsFeatures);
        Assert.False(vm.WindowsFeatures!.OneDrive);
        Assert.True(vm.WindowsFeatures.IsDirty);
    }

    [Fact]
    public async Task An_agent_computer_has_no_family_wide_switches()
    {
        _client.IsHub = false;

        var vm = await LoadedAsync();

        Assert.True(vm.IsAgent);
        Assert.Null(vm.WindowsFeatures);
    }

    [Fact]
    public async Task Diagnostics_explain_a_service_that_is_not_running()
    {
        _client.Failure = new AdminConnectionException(AdminConnectionError.ServiceNotRunning, "no pipe");
        _client.State = AdminConnectionState.ServiceNotRunning;

        var vm = await LoadedAsync();

        Assert.StartsWith("שירות הסינון לא פועל במחשב הזה", vm.ConnectionStateText);
        Assert.Equal("—", vm.DeviceText);
        Assert.Empty(vm.Members);
    }
}

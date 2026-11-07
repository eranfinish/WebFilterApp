using System.IO;
using System.Text.Json.Nodes;
using WebFilter.Admin.Services;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.Tests;

/// <summary>
/// Backup and restore of the family's settings: the file round-trips, files that cannot be restored are named as such and
/// change nothing, the restored settings keep this computer the hub with the account links that belong to it, and the
/// settings page saves and restores through the parent's dialogs.
/// </summary>
public sealed class FamilyBackupTests
{
    private static readonly DateTimeOffset Saved = new(2026, 11, 7, 9, 30, 0, TimeSpan.FromHours(2));

    private static AccountDto Linked(string id, string name, string member) => new(id, name, member, false);

    /// <summary>A family on hub "pc-parents" with one agent "pc-kids"; each computer has an account linked to a child.</summary>
    private static FamilyBundle Family(int version = 40) => TestData.Family() with
    {
        Version = version,
        Devices =
        [
            new DeviceDto("pc-parents", "windows", "hub", [new AccountDto("S-1-5-21-1-500", "dad", null, true), Linked("S-1-5-21-1-1001", "noa-parents", "noa")]),
            new DeviceDto("pc-kids", "windows", "agent", [Linked("S-1-5-21-2-1001", "noa", "noa"), Linked("S-1-5-21-2-1002", "yoav", "yoav")]),
        ],
    };

    private static FamilyBackupFile Backup(FamilyBundle? settings = null) => new(Saved, "pc-parents", "0.7.20", settings ?? Family());

    /// <summary>A fresh hub: only its own entry, with the accounts Windows reported.</summary>
    private static FamilyBundle Fresh(string id, params AccountDto[] reported) =>
        new() { Version = 3, TimeZone = "Asia/Jerusalem", Devices = [new DeviceDto(id, "windows", "hub", reported)] };

    [Fact]
    public void The_file_round_trips_the_settings_and_says_what_it_is()
    {
        var backup = Backup();

        var json = FamilyBackup.Serialize(backup);
        var read = FamilyBackup.Read(json);

        var file = JsonNode.Parse(json)!.AsObject();
        Assert.Equal("webfilter-family-backup", (string?)file["format"]);
        Assert.Equal(1, (int?)file["formatVersion"]);
        Assert.Equal((Saved, "pc-parents", "0.7.20"), (read.CreatedAt, read.HubDeviceId, read.AppVersion));
        Assert.Equal(FamilyBundleJson.Serialize(backup.Settings), FamilyBundleJson.Serialize(read.Settings));
        Assert.Equal("WebFilter-backup-2026-11-07.json", FamilyBackup.SuggestedFileName(Saved));
    }

    [Theory]
    [InlineData("not json at all", BackupProblem.NotABackup)]
    [InlineData("[1, 2]", BackupProblem.NotABackup)]
    [InlineData("""{"format":"something-else","formatVersion":1}""", BackupProblem.NotABackup)]
    [InlineData("""{"format":"webfilter-family-backup","formatVersion":2,"createdAt":"2026-11-07T09:30:00+02:00","hubDeviceId":"pc","settings":{"timeZone":"UTC"}}""", BackupProblem.TooNew)]
    [InlineData("""{"format":"webfilter-family-backup","formatVersion":1,"createdAt":"2026-11-07T09:30:00+02:00","hubDeviceId":"pc"}""", BackupProblem.Damaged)]
    [InlineData("""{"format":"webfilter-family-backup","formatVersion":1,"createdAt":"2026-11-07T09:30:00+02:00","hubDeviceId":"pc","settings":{"members":"x"}}""", BackupProblem.Damaged)]
    [InlineData("""{"format":"webfilter-family-backup","createdAt":"2026-11-07T09:30:00+02:00","hubDeviceId":"pc","settings":{"timeZone":"UTC"}}""", BackupProblem.Damaged)]
    public void A_file_that_cannot_be_restored_says_why(string json, BackupProblem problem)
    {
        var ex = Assert.Throws<FamilyBackupException>(() => FamilyBackup.Read(json));
        Assert.Equal(problem, ex.Problem);
    }

    [Fact]
    public void Restored_on_the_same_hub_after_a_reinstall_the_links_come_back()
    {
        // A reinstalled hub reports its accounts again, unlinked; the backup knows whose they are.
        var current = Fresh("pc-parents", new AccountDto("S-1-5-21-1-500", "dad", null, true), new AccountDto("S-1-5-21-1-1001", "noa-parents", null, false));

        var restored = FamilyBackup.ForThisHub(Backup(), current, "pc-parents");

        Assert.Equal(3, restored.Version); // the hub's own version, so the service takes it as the next change
        Assert.Equal(["pc-parents", "pc-kids"], restored.Devices.Select(d => d.Id));
        Assert.Equal(["hub", "agent"], restored.Devices.Select(d => d.Role));
        Assert.Equal("noa", restored.Devices[0].Accounts.Single(a => a.Name == "noa-parents").Member);
        Assert.Equal(2, restored.Devices[0].Accounts.Count);
        Assert.Equal(Family().Members, restored.Members);
        Assert.Equal(Family().Rules, restored.Rules);
    }

    [Fact]
    public void A_former_agent_that_becomes_the_hub_keeps_its_links_and_the_old_hub_waits_as_an_agent()
    {
        var current = Fresh("pc-kids", new AccountDto("S-1-5-21-2-1001", "noa", null, false));

        var restored = FamilyBackup.ForThisHub(Backup(), current, "pc-kids");

        Assert.Equal([("pc-kids", "hub"), ("pc-parents", "agent")], restored.Devices.Select(d => (d.Id, d.Role)));
        Assert.Equal(["noa", "yoav"], restored.Devices[0].Accounts.Select(a => a.Member));
        // Pairing the old parents' computer as an agent later brings its own links back (the hub keeps known entries).
        Assert.Equal("noa", restored.Devices[1].Accounts.Single(a => a.Name == "noa-parents").Member);
        Assert.Single(restored.Devices, d => d.Role == "hub");
    }

    [Fact]
    public void A_new_computer_as_the_hub_starts_with_its_accounts_unlinked()
    {
        var current = Fresh("pc-new", new AccountDto("S-1-5-21-3-500", "mom", null, true), new AccountDto("S-1-5-21-3-1001", "kid", null, false));

        var restored = FamilyBackup.ForThisHub(Backup(), current, "pc-new");

        Assert.Equal([("pc-new", "hub"), ("pc-parents", "agent"), ("pc-kids", "agent")], restored.Devices.Select(d => (d.Id, d.Role)));
        Assert.All(restored.Devices[0].Accounts, a => Assert.Null(a.Member));
        Assert.Equal(["mom", "kid"], restored.Devices[0].Accounts.Select(a => a.Name));
    }

    [Fact]
    public void Accounts_the_hub_knows_now_and_the_backup_does_not_stay_unlinked()
    {
        var current = Fresh("pc-parents", new AccountDto("S-1-5-21-1-1009", "guest", "noa", false, Guest: true));

        var restored = FamilyBackup.ForThisHub(Backup(), current, "pc-parents");

        var guest = restored.Devices[0].Accounts.Single(a => a.Name == "guest");
        Assert.Null(guest.Member);
        Assert.False(guest.Guest);
    }

    // ---- the settings page ---------------------------------------------------------------------------------------

    private readonly FakeAdminClient _client = new();
    private readonly FakeDialogs _dialogs = new();
    private readonly FakeFiles _files = new();

    private async Task<SettingsViewModel> PageAsync()
    {
        var vm = new SettingsViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)), _dialogs, _files, _files);
        await vm.RefreshAsync();
        return vm;
    }

    [Fact]
    public async Task Save_writes_the_current_settings_where_the_parent_chose()
    {
        _client.Settings = Family(version: _client.Settings.Version);
        var vm = await PageAsync();

        await vm.SaveBackupCommand.ExecuteAsync(null);

        var saved = FamilyBackup.Read(_files.Files[@"D:\backups\family.json"]);
        Assert.Equal("pc-parents", saved.HubDeviceId);
        Assert.Equal(FamilyBundleJson.Serialize(_client.Settings), FamilyBundleJson.Serialize(saved.Settings));
        Assert.StartsWith("WebFilter-backup-", _files.SuggestedName, StringComparison.Ordinal);
        Assert.Contains("הגיבוי נשמר", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Cancelled_or_failed_saves_say_so_and_write_nothing()
    {
        var vm = await PageAsync();
        _files.Chosen = null;
        await vm.SaveBackupCommand.ExecuteAsync(null);
        Assert.Empty(_files.Files);
        Assert.Null(vm.StatusMessage);

        _files.Chosen = @"E:\full.json";
        _files.Failure = new IOException("The disk is full.");
        await vm.SaveBackupCommand.ExecuteAsync(null);
        Assert.Empty(_files.Files);
        Assert.Equal("לא הצלחנו לשמור את הגיבוי: The disk is full.", vm.ErrorMessage);
    }

    [Fact]
    public async Task Restore_asks_first_then_replaces_the_settings_keeping_this_computer_the_hub()
    {
        _files.Files[@"D:\backups\family.json"] = FamilyBackup.Serialize(Backup());
        var vm = await PageAsync();
        var before = _client.Settings.Version;

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        var question = Assert.Single(_dialogs.Asked);
        Assert.Contains($"{Family().Members.Count} בני משפחה", question, StringComparison.Ordinal);
        Assert.Contains("2 מחשבים", question, StringComparison.Ordinal);
        var replace = _client.Single<ReplaceSettings>();
        Assert.Equal(before, replace.IfVersion);
        Assert.Equal(("pc-parents", "hub"), (replace.Settings.Devices[0].Id, replace.Settings.Devices[0].Role));
        Assert.Equal(Family().Members, replace.Settings.Members);
        Assert.Contains("שוחזרו מהגיבוי", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Restore_changes_nothing_when_the_parent_says_no()
    {
        _files.Files[@"D:\backups\family.json"] = FamilyBackup.Serialize(Backup());
        _dialogs.Answer = false;
        var vm = await PageAsync();

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Empty(_client.SentOf<ReplaceSettings>());
        Assert.Null(vm.StatusMessage);
    }

    [Theory]
    [InlineData("{}", "הקובץ הזה אינו גיבוי של WebFilter. לא שוחזר דבר.")]
    [InlineData("""{"format":"webfilter-family-backup","formatVersion":9}""", "הגיבוי נוצר בגרסה חדשה יותר של WebFilter. עדכנו קודם את WebFilter במחשב הזה. לא שוחזר דבר.")]
    public async Task A_file_that_cannot_be_restored_is_named_and_nothing_is_asked_or_sent(string json, string error)
    {
        _files.Files[@"D:\backups\family.json"] = json;
        var vm = await PageAsync();

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal(error, vm.ErrorMessage);
        Assert.Empty(_dialogs.Asked);
        Assert.Empty(_client.SentOf<ReplaceSettings>());
    }

    [Fact]
    public async Task A_refusal_from_the_service_is_shown_and_nothing_else_changes()
    {
        _files.Files[@"D:\backups\family.json"] = FamilyBackup.Serialize(Backup());
        _client.Reply = m => m is ReplaceSettings ? new AdminError("invalid_settings", "Rule r-1: the pattern is not valid.") : null;
        var vm = await PageAsync();

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task On_an_agent_computer_there_is_nothing_to_back_up()
    {
        _client.IsHub = false;
        var vm = await PageAsync();

        Assert.False(vm.SaveBackupCommand.CanExecute(null));
        Assert.False(vm.RestoreBackupCommand.CanExecute(null));
    }

    // ---- automatic backups ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Automatic_backup_starts_off_and_a_chosen_folder_turns_it_on()
    {
        var vm = await PageAsync();
        Assert.True(vm.AutoBackupAvailable);
        Assert.False(vm.AutoBackupOn);
        Assert.Equal("הגיבוי האוטומטי כבוי.", vm.AutoBackupText);
        Assert.False(vm.TurnOffAutoBackupCommand.CanExecute(null));

        await vm.ChooseAutoBackupFolderCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Users\dad\OneDrive\WebFilter", _client.Single<SetAutoBackup>().Folder);
        Assert.True(vm.AutoBackupOn);
        Assert.Contains(@"C:\Users\dad\OneDrive\WebFilter", vm.AutoBackupText, StringComparison.Ordinal);
        Assert.Contains("גיבוי אחרון:", vm.AutoBackupText, StringComparison.Ordinal);
        Assert.Contains("הגיבוי האוטומטי פועל", vm.StatusMessage, StringComparison.Ordinal);
        Assert.True(vm.TurnOffAutoBackupCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelling_the_folder_dialog_changes_nothing()
    {
        _files.ChosenFolder = null;
        var vm = await PageAsync();

        await vm.ChooseAutoBackupFolderCommand.ExecuteAsync(null);

        Assert.Empty(_client.SentOf<SetAutoBackup>());
    }

    [Theory]
    [InlineData(SetAutoBackup.NetworkCode, "x", "אי אפשר לגבות לתיקייה ברשת. בחרו תיקייה במחשב הזה — למשל בתוך OneDrive.")]
    [InlineData(SetAutoBackup.MissingCode, "x", "התיקייה הזו כבר לא קיימת.")]
    [InlineData(SetAutoBackup.WriteFailedCode, "Access is denied.", "לא הצלחנו לשמור בתיקייה הזו: Access is denied.")]
    public async Task A_refused_folder_is_explained_and_backups_stay_as_they_were(string code, string detail, string shown)
    {
        _client.AutoBackupRefusal = new AdminError(code, detail);
        var vm = await PageAsync();

        await vm.ChooseAutoBackupFolderCommand.ExecuteAsync(null);

        Assert.Equal(shown, vm.ErrorMessage);
        Assert.False(vm.AutoBackupOn);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task A_failing_backup_is_shown_until_one_succeeds_and_it_can_be_turned_off()
    {
        _client.AutoBackup = new AutoBackupResult(@"D:\Backups", TestData.Now.AddDays(-2), "The device is not ready.");
        var vm = await PageAsync();
        Assert.Equal("הגיבוי האחרון נכשל: The device is not ready. ננסה שוב בעוד כמה דקות.", vm.AutoBackupProblem);
        Assert.True(vm.HasAutoBackupProblem);

        await vm.TurnOffAutoBackupCommand.ExecuteAsync(null);

        Assert.Null(_client.Single<SetAutoBackup>().Folder);
        Assert.False(vm.AutoBackupOn);
        Assert.False(vm.HasAutoBackupProblem);
        Assert.Equal("הגיבוי האוטומטי כובה. קבצים שכבר נשמרו נשארים בתיקייה.", vm.StatusMessage);
    }

    [Fact]
    public async Task The_folder_dialog_opens_on_the_current_folder()
    {
        _client.AutoBackup = new AutoBackupResult(@"D:\Backups", TestData.Now, null);
        var vm = await PageAsync();

        await vm.ChooseAutoBackupFolderCommand.ExecuteAsync(null);

        Assert.Equal(@"D:\Backups", _files.OfferedFolder);
    }

    [Fact]
    public async Task An_older_service_without_automatic_backups_hides_the_section()
    {
        _client.Reply = m => m is GetAutoBackup ? new AdminError("unsupported", "Unsupported message type GetAutoBackup.") : null;
        var vm = await PageAsync();

        Assert.False(vm.AutoBackupAvailable);
        Assert.False(vm.ChooseAutoBackupFolderCommand.CanExecute(null));
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Without_the_dialogs_the_buttons_stay_off()
    {
        var vm = new SettingsViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));
        await vm.RefreshAsync();

        Assert.False(vm.SaveBackupCommand.CanExecute(null));
    }
}

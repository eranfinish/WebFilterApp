using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.Tests;

public sealed class SetupWizardViewModelTests
{
    private readonly FakeAdminClient _client = new();
    private int _nextId;

    public SetupWizardViewModelTests()
    {
        // A fresh hub: an empty family and this computer without known accounts (MVP-PLAN §4.5).
        _client.Settings = EmptyFamily();
        _client.Status = TestData.Status(1) with { Members = [] };
        _client.LocalAccounts.AddRange(
        [
            new AccountDto("S-1-5-21-9-1001", "Dad", null, IsAdmin: true),
            new AccountDto("S-1-5-21-9-1002", "noa", null, IsAdmin: false),
            new AccountDto("S-1-5-21-9-1003", "yoav", null, IsAdmin: false),
        ]);
    }

    public static FamilyBundle EmptyFamily() => new()
    {
        Version = 1,
        TimeZone = "Asia/Jerusalem",
        Devices = [new DeviceDto("pc-parents", "windows", "hub", [])],
    };

    private async Task<SetupWizardViewModel> LoadedAsync()
    {
        var vm = new SetupWizardViewModel(new FamilyContext(_client, new ManualTime(TestData.Now))) { NewMemberId = () => $"m-{++_nextId}" };
        await vm.RefreshAsync();
        return vm;
    }

    /// <summary>Noa (child, 8), Yoav (teen, 14) and Dad (adult), as a parent would type them.</summary>
    private static void TypeFamily(SetupWizardViewModel vm)
    {
        vm.Members[0].Name = "נועה";
        vm.Members[0].AgeText = "8";
        vm.AddMemberCommand.Execute(null);
        vm.Members[1].Name = " יואב ";
        vm.Members[1].AgeText = "14";
        vm.AddMemberCommand.Execute(null);
        vm.Members[2].Name = "אבא";
        vm.Members[2].Role = "adult";
    }

    private static WizardAccountViewModel Account(SetupWizardViewModel vm, string name) => vm.Accounts.Single(a => a.Name == name);

    [Fact]
    public async Task Starts_with_one_child_on_a_fresh_hub()
    {
        var vm = await LoadedAsync();

        Assert.True(vm.ShowsWizard);
        Assert.False(vm.IsAlreadySetUp);
        Assert.True(vm.IsMembersStep);
        Assert.False(vm.CanGoBack);
        var first = Assert.Single(vm.Members);
        Assert.Equal(("child", "allowlist", "allowlist", true), (first.Role, first.FilterMode, first.YouTubeMode, first.LimitTime));
        Assert.Equal(["1. בני משפחה", "2. חשבונות Windows", "3. שעות ומכסה", "4. סיכום"], vm.Steps.Select(s => s.Label));
        Assert.True(vm.Steps[0].IsCurrent);
    }

    [Fact]
    public async Task Walks_through_the_family_and_applies_it_in_one_replacement()
    {
        var vm = await LoadedAsync();
        TypeFamily(vm);
        var (noa, yoav, dad) = (vm.Members[0].Id, vm.Members[1].Id, vm.Members[2].Id);

        // 2. Accounts: this computer's accounts are read first, standard accounts before administrators.
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.IsAccountsStep);
        Assert.Single(_client.SentOf<ScanAccounts>());
        Assert.Equal(["noa", "yoav", "Dad"], vm.Accounts.Select(a => a.Name));
        Assert.All(vm.Accounts, a => Assert.Equal(SetupWizardViewModel.NotLinked, a.MemberId));
        Assert.Equal(["לא מקושר", "נועה", "יואב", "אבא"], vm.LinkOptions.Select(o => o.Label));
        Account(vm, "noa").MemberId = noa;
        Account(vm, "yoav").MemberId = yoav;
        Account(vm, "Dad").MemberId = dad;
        Assert.All(vm.Accounts, a => Assert.Null(a.Warning));

        // 3. Hours: only the filtered members, starting from their role's template.
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.IsTimeStep);
        Assert.Equal([noa, yoav], vm.TimedMembers.Select(m => m.Id));
        Assert.Equal(("15:00", "19:00", "120"), (vm.TimedMembers[0].Start, vm.TimedMembers[0].End, vm.TimedMembers[0].LimitText));
        Assert.Equal(("14:00", "21:30", "240"), (vm.TimedMembers[1].Start, vm.TimedMembers[1].End, vm.TimedMembers[1].LimitText));
        vm.TimedMembers[1].LimitText = "180";

        // 4. Review.
        await vm.NextCommand.ExecuteAsync(null);
        Assert.True(vm.IsReviewStep);
        Assert.Equal("ילד/ה, גיל 8 · רק אתרים מאושרים · YouTube: רק ערוצים מאושרים · 15:00–19:00, עד 120 דקות ביום · חשבון: noa", Bidi.Plain(vm.Members[0].SummaryText));
        Assert.Equal("מבוגר/ת · ללא סינון · YouTube: ללא הגבלה · בלי הגבלת זמן · חשבון: Dad", vm.Members[2].SummaryText);
        Assert.Null(vm.AccountsSummary);

        await vm.ApplyCommand.ExecuteAsync(null);

        var replace = Assert.Single(_client.SentOf<ReplaceSettings>());
        Assert.Equal(2, replace.IfVersion); // the scan added the accounts (version 2)
        Assert.Equal(
            [new MemberDto(noa, "נועה", "child", 8, "allowlist", "allowlist"), new MemberDto(yoav, "יואב", "teen", 14, "blocklist", "restricted"),
             new MemberDto(dad, "אבא", "adult", null, "unrestricted", "unrestricted")],
            replace.Settings.Members);
        Assert.Equal(new Dictionary<string, string?> { ["noa"] = noa, ["yoav"] = yoav, ["Dad"] = dad },
            replace.Settings.Devices.Single(d => d.Id == "pc-parents").Accounts.ToDictionary(a => a.Name, a => a.Member));
        Assert.Equal([$"{noa}:0,1,2,3,4,5,6:15:00-19:00", $"{yoav}:0,1,2,3,4,5,6:14:00-21:30"],
            replace.Settings.Schedules.Select(s => $"{s.Member}:{string.Join(",", s.Days)}:{s.Start}-{s.End}"));
        Assert.Equal([$"{noa}:120", $"{yoav}:180"], replace.Settings.DailyLimits.Select(l => $"{l.Member}:{l.Minutes}"));
        Assert.True(vm.IsDone);
        Assert.True(vm.ShowsWizard);
        Assert.False(vm.ShowsAlreadySetUp);
        Assert.Null(vm.ErrorMessage);
    }

    [Theory]
    [InlineData("teen", "blocklist", "restricted", true, "14:00", "21:30", "240")]
    [InlineData("adult", "unrestricted", "unrestricted", false, "08:00", "22:00", "180")]
    [InlineData("child", "allowlist", "allowlist", true, "15:00", "19:00", "120")]
    public async Task A_role_brings_its_template(string role, string filter, string youTube, bool limitTime, string start, string end, string limit)
    {
        var vm = await LoadedAsync();
        var member = vm.Members[0];
        member.Role = "adult";

        member.Role = role;

        Assert.Equal((filter, youTube, limitTime, start, end, limit), (member.FilterMode, member.YouTubeMode, member.LimitTime, member.Start, member.End, member.LimitText));
        Assert.Equal(filter != "unrestricted", member.IsFiltered);
    }

    [Theory]
    [InlineData("", "", "", "כתבו שם לכל בן משפחה.")]
    [InlineData("נועה", "", "נועה", "כבר יש בן משפחה בשם נועה.")]
    [InlineData("נועה", "0", "יואב", "גיל הוא מספר בין 1 ל-120, או השאירו ריק.")]
    [InlineData("נועה", "121", "יואב", "גיל הוא מספר בין 1 ל-120, או השאירו ריק.")]
    public async Task Members_are_checked_before_going_on(string first, string age, string second, string message)
    {
        var vm = await LoadedAsync();
        vm.Members[0].Name = first;
        vm.Members[0].AgeText = age;
        vm.AddMemberCommand.Execute(null);
        vm.Members[1].Name = second;

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(message, vm.Message);
        Assert.True(vm.IsMembersStep);
        Assert.Empty(_client.SentOf<ScanAccounts>());
    }

    [Fact]
    public async Task The_last_member_cannot_be_removed()
    {
        var vm = await LoadedAsync();
        vm.AddMemberCommand.Execute(null);

        vm.RemoveMemberCommand.Execute(vm.Members[0]);
        vm.RemoveMemberCommand.Execute(vm.Members[0]);

        var left = Assert.Single(vm.Members);
        Assert.Equal("teen", left.Role);
    }

    [Fact]
    public async Task An_administrator_account_linked_to_a_filtered_member_is_warned_about()
    {
        var vm = await LoadedAsync();
        TypeFamily(vm);
        await vm.NextCommand.ExecuteAsync(null);

        Account(vm, "Dad").MemberId = vm.Members[0].Id;

        Assert.Equal("Dad הוא חשבון מנהל: WebFilter לא מסנן מנהלים, ומנהל יכול לכבות את הסינון. תנו ל-נועה חשבון Windows רגיל.", Account(vm, "Dad").Warning);
        Account(vm, "Dad").MemberId = vm.Members[2].Id;
        Assert.Null(Account(vm, "Dad").Warning);
    }

    [Fact]
    public async Task Looking_again_finds_a_new_account_and_keeps_the_links_already_chosen()
    {
        var vm = await LoadedAsync();
        TypeFamily(vm);
        await vm.NextCommand.ExecuteAsync(null);
        Account(vm, "noa").MemberId = vm.Members[0].Id;
        _client.LocalAccounts.Add(new AccountDto("S-1-5-21-9-1004", "guest", null, IsAdmin: false));

        await vm.RescanCommand.ExecuteAsync(null);

        Assert.Equal(["guest", "noa", "yoav", "Dad"], vm.Accounts.Select(a => a.Name));
        Assert.Equal(vm.Members[0].Id, Account(vm, "noa").MemberId);
        Assert.Equal(SetupWizardViewModel.NotLinked, Account(vm, "guest").MemberId);
        Assert.Equal(2, _client.SentOf<ScanAccounts>().Count());
    }

    [Fact]
    public async Task Without_accounts_on_this_computer_the_step_says_so()
    {
        _client.LocalAccounts.Clear();
        var vm = await LoadedAsync();
        vm.Members[0].Name = "נועה";

        await vm.NextCommand.ExecuteAsync(null);

        Assert.True(vm.HasNoAccounts);
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Null(vm.AccountsSummary);
        Assert.Contains("אין חשבון במחשב הזה", vm.Members[0].SummaryText);
    }

    [Fact]
    public async Task Going_back_keeps_what_was_entered_and_a_renamed_member_keeps_their_account()
    {
        var vm = await LoadedAsync();
        TypeFamily(vm);
        await vm.NextCommand.ExecuteAsync(null);
        Account(vm, "noa").MemberId = vm.Members[0].Id;
        Account(vm, "yoav").MemberId = vm.Members[1].Id;

        vm.BackCommand.Execute(null);
        Assert.True(vm.IsMembersStep);
        vm.Members[0].Name = "נועי";
        vm.RemoveMemberCommand.Execute(vm.Members[1]);
        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(["לא מקושר", "נועי", "אבא"], vm.LinkOptions.Select(o => o.Label));
        Assert.Equal(vm.Members[0].Id, Account(vm, "noa").MemberId);
        Assert.Equal(SetupWizardViewModel.NotLinked, Account(vm, "yoav").MemberId);
    }

    [Theory]
    [InlineData("4pm", "19:00", "120", "נועה: כתבו שעות בצורה 16:30 (סוף היום: 24:00).")]
    [InlineData("19:00", "15:00", "120", "נועה: השעה 19:00 צריכה להיות לפני 15:00.")]
    [InlineData("15:00", "19:00", "-5", "נועה: המכסה היומית היא מספר דקות בין 0 ל-1440, או השאירו ריק.")]
    public async Task Hours_are_checked_before_the_summary(string start, string end, string limit, string message)
    {
        var vm = await LoadedAsync();
        vm.Members[0].Name = "נועה";
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.TimedMembers[0].Start = start;
        vm.TimedMembers[0].End = end;
        vm.TimedMembers[0].LimitText = limit;

        await vm.NextCommand.ExecuteAsync(null);

        Assert.Equal(message, vm.Message);
        Assert.True(vm.IsTimeStep);
    }

    [Fact]
    public async Task Hours_without_a_daily_limit_and_a_member_without_time_limits()
    {
        var vm = await LoadedAsync();
        vm.Members[0].Name = "נועה";
        vm.AddMemberCommand.Execute(null);
        vm.Members[1].Name = "יואב";
        await vm.NextCommand.ExecuteAsync(null);
        await vm.NextCommand.ExecuteAsync(null);
        vm.TimedMembers[0].LimitText = "";
        vm.TimedMembers[1].LimitTime = false;
        await vm.NextCommand.ExecuteAsync(null);
        Assert.Contains("15:00–19:00, בלי מכסה", Bidi.Plain(vm.Members[0].SummaryText));
        Assert.Contains("בכל שעה, בלי מכסה", vm.Members[1].SummaryText);

        await vm.ApplyCommand.ExecuteAsync(null);

        var settings = _client.Single<ReplaceSettings>().Settings;
        Assert.Equal([vm.Members[0].Id], settings.Schedules.Select(s => s.Member));
        Assert.Empty(settings.DailyLimits);
    }

    [Fact]
    public async Task A_refused_setup_stays_on_the_summary_with_the_reason()
    {
        _client.Reply = m => m is ReplaceSettings ? new AdminError("bad_request", "Two family members have the id 'm-1'.") : null;
        var vm = await LoadedAsync();
        vm.Members[0].Name = "נועה";
        for (var i = 0; i < 3; i++) await vm.NextCommand.ExecuteAsync(null);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.IsReviewStep);
        Assert.Equal("שירות הסינון לא ביצע את הפעולה: Two family members have the id 'm-1'.", vm.ErrorMessage);
    }

    [Fact]
    public async Task A_family_that_is_already_set_up_is_pointed_to_the_member_page()
    {
        _client.Settings = TestData.Family();
        _client.Status = TestData.Status();

        var vm = await LoadedAsync();

        Assert.True(vm.IsAlreadySetUp);
        Assert.True(vm.ShowsAlreadySetUp);
        Assert.False(vm.ShowsWizard);
    }

    [Fact]
    public async Task On_an_agent_computer_setup_is_done_on_the_hub()
    {
        _client.Reply = m => m switch
        {
            GetStatus => _client.Status with { IsHub = false },
            GetSettings => new AdminError("not_hub", "This computer is managed by the hub computer; make this change there."),
            _ => null,
        };

        var vm = await LoadedAsync();

        Assert.True(vm.IsNotHub);
        Assert.False(vm.ShowsWizard);
        Assert.False(vm.ShowsAlreadySetUp);
        Assert.Null(vm.ErrorMessage);
    }
}

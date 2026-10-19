using WebFilter.Admin.Resources;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.Tests;

public sealed class MemberDetailViewModelTests
{
    private readonly FakeAdminClient _client = new();
    private readonly FakeDialogs _dialogs = new();

    public MemberDetailViewModelTests() =>
        // Like the service: every change bumps the settings version, so the page reads them again.
        _client.Reply = m =>
        {
            if (m is AddRule or RemoveRule or SetMember) _client.BumpVersion();
            return null;
        };

    private async Task<MemberDetailViewModel> LoadedAsync(string? member = null)
    {
        var vm = new MemberDetailViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)), _dialogs)
        {
            NewRuleId = () => "r-test",
            NewAppRuleId = () => "a-test",
        };
        if (member is not null) vm.Show(member);
        await vm.RefreshAsync();
        return vm;
    }

    private ReplaceSettings LastReplace => _client.SentOf<ReplaceSettings>().Last();

    [Fact]
    public async Task Shows_the_first_member_with_every_section()
    {
        var vm = await LoadedAsync();

        Assert.Equal(["noa", "yoav", "dana", "savta"], vm.MemberOptions.Select(o => o.Value));
        Assert.Equal("noa", vm.SelectedMemberId);
        Assert.True(vm.HasMember);
        Assert.False(vm.IsUnrestricted);

        var profile = vm.Profile!;
        Assert.Equal(("נועה", "child", "8", "allowlist", "allowlist"), (profile.DisplayName, profile.Role, profile.AgeText, profile.FilterMode, profile.YouTubeMode));
        Assert.False(profile.IsDirty);

        var week = vm.Week!;
        Assert.True(week.RestrictsHours);
        Assert.Equal(["יום ראשון", "יום שני", "יום שלישי", "יום רביעי", "יום חמישי", "יום שישי", "יום שבת"], week.Days.Select(d => d.Name));
        Assert.Equal(["15:00-19:00"], Windows(week.Days[0]));
        Assert.Equal(["09:00-13:00", "16:00-20:00"], Windows(week.Days[5]));
        Assert.Equal(["120", "120", "120", "120", "120", "180", "180"], week.Days.Select(d => d.LimitText));
        Assert.False(week.IsDirty);
        Assert.All(week.Days, d => Assert.False(d.IsClosed));

        Assert.Equal(["Khan Kids (sim)", "Sesame (sim)"], vm.Channels.Select(c => c.Name));
        Assert.Equal("@khankids-sim · UCkhanKids00000000000001", vm.Channels[0].DetailText);
        Assert.True(vm.Channels[0].IsAllow);
        Assert.True(vm.HasNoApps);
        Assert.True(vm.HasNoRules);

        Assert.Equal(new Dictionary<string, string> { ["educational"] = "לימודים", ["social"] = "רשתות חברתיות", ["gaming"] = "משחקים" },
            vm.Categories!.Items.ToDictionary(c => c.Name, c => c.Label));
        Assert.Equal("allow", vm.Categories.Items.Single(c => c.Name == "educational").Choice);
        Assert.Equal(CategoryChoiceViewModel.Default, vm.Categories.Items.Single(c => c.Name == "social").Choice);
        Assert.Equal("khanacademy.org, wikipedia.org, scratch.mit.edu", vm.Categories.Items.Single(c => c.Name == "educational").DomainsText);
    }

    [Fact]
    public async Task Show_opens_another_member()
    {
        var vm = await LoadedAsync();

        vm.Show("yoav");

        Assert.Equal("יואב", vm.Profile!.DisplayName);
        Assert.Equal(["Microsoft.MinecraftUWP", "minecraft.exe"], vm.Apps.Select(a => a.Name));
        Assert.Equal(["אפליקציה מהחנות", "תוכנה (נתיב)"], vm.Apps.Select(a => a.MatchText));
        Assert.Equal(["GamerX (sim)"], vm.Channels.Select(c => c.Name));
        Assert.False(vm.Channels[0].IsAllow);
        Assert.Equal(["block", "block"], vm.Categories!.Items.Where(c => c.Name != "educational").Select(c => c.Choice));
        Assert.Single(_client.SentOf<GetSettings>());
    }

    [Fact]
    public async Task Another_member_starts_without_the_last_members_form_messages()
    {
        var vm = await LoadedAsync();
        vm.NewPattern = "games";
        await vm.AddRuleCommand.ExecuteAsync(null);
        vm.NewChannel = "@khankids";
        await vm.AddChannelCommand.ExecuteAsync(null);
        Assert.NotNull(vm.RuleMessage);
        Assert.NotNull(vm.ChannelMessage);

        vm.Show("yoav");

        Assert.Null(vm.RuleMessage);
        Assert.Null(vm.ChannelMessage);
        Assert.Null(vm.AppMessage);
    }

    [Fact]
    public async Task An_unfiltered_member_is_marked_so()
    {
        var vm = await LoadedAsync("dana");

        Assert.True(vm.IsUnrestricted);
        Assert.False(vm.Week!.RestrictsHours);
        Assert.All(vm.Week.Days, d => Assert.False(d.HoursEnabled));
    }

    // ---- week ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Saving_the_week_replaces_only_this_members_hours_at_the_version_read()
    {
        var vm = await LoadedAsync();
        var week = vm.Week!;
        week.Days[1].Windows[0].End = "20:00";
        week.Days[6].LimitText = "";
        Assert.True(week.IsDirty);
        Assert.True(week.SaveCommand.CanExecute(null));

        await week.SaveCommand.ExecuteAsync(null);

        var replace = Assert.Single(_client.SentOf<ReplaceSettings>());
        Assert.Equal(7, replace.IfVersion);
        Assert.Equal(
            ["noa:0,2,3,4:15:00-19:00", "noa:1:15:00-20:00", "noa:5:09:00-13:00", "noa:5:16:00-20:00", "noa:6:09:00-20:00"],
            replace.Settings.Schedules.Where(s => s.Member == "noa").Select(Text));
        Assert.Equal(["noa:0,1,2,3,4:120", "noa:5:180"], replace.Settings.DailyLimits.Where(l => l.Member == "noa").Select(Text));
        Assert.Equal(TestData.Family().Schedules.Where(s => s.Member == "yoav").Select(Text), replace.Settings.Schedules.Where(s => s.Member == "yoav").Select(Text));
        Assert.Equal(TestData.Family().Rules, replace.Settings.Rules);
        Assert.Equal("השעות והמכסה של נועה נשמרו.", vm.StatusMessage);
        // Reloaded from the stored settings: nothing left to save.
        Assert.False(vm.Week!.IsDirty);
        Assert.Equal(["15:00-20:00"], Windows(vm.Week.Days[1]));
        Assert.Equal("", vm.Week.Days[6].LimitText);
    }

    [Theory]
    [InlineData(2, "25:00", "18:00", "", "יום שלישי: כתבו שעות בצורה 16:30 (סוף היום: 24:00).")]
    [InlineData(3, "18:00", "17:00", "", "יום רביעי: השעה 18:00 צריכה להיות לפני 17:00.")]
    [InlineData(4, "15:00", "19:00", "abc", "יום חמישי: המכסה היומית היא מספר דקות בין 0 ל-1440, או השאירו ריק.")]
    [InlineData(4, "15:00", "19:00", "1441", "יום חמישי: המכסה היומית היא מספר דקות בין 0 ל-1440, או השאירו ריק.")]
    public async Task Hours_and_limits_that_do_not_make_sense_are_explained_and_not_sent(int day, string start, string end, string limit, string message)
    {
        var vm = await LoadedAsync();
        var editor = vm.Week!.Days[day];
        editor.Windows[0].Start = start;
        editor.Windows[0].End = end;
        editor.LimitText = limit;

        await vm.Week.SaveCommand.ExecuteAsync(null);

        Assert.Equal(message, vm.Week.Message);
        Assert.Empty(_client.SentOf<ReplaceSettings>());
        Assert.True(vm.Week.IsDirty);
    }

    [Fact]
    public async Task Without_hour_restrictions_the_schedules_go_and_the_limits_stay()
    {
        var vm = await LoadedAsync();
        vm.Week!.RestrictsHours = false;
        Assert.All(vm.Week.Days, d => Assert.False(d.HoursEnabled));

        await vm.Week.SaveCommand.ExecuteAsync(null);

        Assert.DoesNotContain(LastReplace.Settings.Schedules, s => s.Member == "noa");
        Assert.Equal(["noa:0,1,2,3,4:120", "noa:5,6:180"], LastReplace.Settings.DailyLimits.Where(l => l.Member == "noa").Select(Text));
    }

    [Fact]
    public async Task Restricting_hours_where_there_were_none_starts_from_a_window_every_day()
    {
        var vm = await LoadedAsync("savta");

        vm.Week!.RestrictsHours = true;

        Assert.All(vm.Week.Days, d => Assert.Equal(["08:00-20:00"], Windows(d)));
        Assert.True(vm.Week.IsDirty);

        vm.Week.Days[6].RemoveWindowCommand.Execute(vm.Week.Days[6].Windows[0]);
        Assert.True(vm.Week.Days[6].IsClosed);
        vm.Week.Days[0].AddWindowCommand.Execute(null);
        Assert.Equal(["08:00-20:00", "20:00-22:00"], Windows(vm.Week.Days[0]));

        vm.Week.RevertCommand.Execute(null);
        Assert.False(vm.Week.RestrictsHours);
        Assert.All(vm.Week.Days, d => Assert.Empty(d.Windows));
        Assert.False(vm.Week.IsDirty);
    }

    [Fact]
    public async Task One_days_hours_and_limit_can_be_copied_to_every_day()
    {
        var vm = await LoadedAsync();

        vm.Week!.CopyToAllCommand.Execute(vm.Week.Days[6]);

        Assert.All(vm.Week.Days, d => Assert.Equal(["09:00-20:00"], Windows(d)));
        Assert.All(vm.Week.Days, d => Assert.Equal("180", d.LimitText));
        await vm.Week.SaveCommand.ExecuteAsync(null);
        Assert.Equal(["noa:0,1,2,3,4,5,6:09:00-20:00"], LastReplace.Settings.Schedules.Where(s => s.Member == "noa").Select(Text));
        Assert.Equal(["noa:0,1,2,3,4,5,6:180"], LastReplace.Settings.DailyLimits.Where(l => l.Member == "noa").Select(Text));
    }

    [Fact]
    public async Task A_change_made_meanwhile_is_read_again_and_kept()
    {
        var vm = await LoadedAsync();
        var approved = new RuleDto("req-1", "noa", "pbskids.org", "domain", "allow");
        var first = true;
        _client.Reply = m =>
        {
            // Just before the first replacement arrives, a request is approved (another window, or the hub itself).
            if (m is ReplaceSettings && first)
            {
                first = false;
                _client.BumpVersion(_client.Settings with { Rules = [.. _client.Settings.Rules, approved] });
            }
            return null;
        };
        vm.Week!.Days[0].LimitText = "90";

        await vm.Week.SaveCommand.ExecuteAsync(null);

        Assert.Equal([7, 8], _client.SentOf<ReplaceSettings>().Select(r => r.IfVersion));
        Assert.Contains(approved, _client.Settings.Rules);
        Assert.Contains(_client.Settings.DailyLimits, l => l.Member == "noa" && l.Days.SequenceEqual([0]) && l.Minutes == 90);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(vm.Week!.Message);
    }

    [Fact]
    public async Task Settings_that_keep_changing_are_reported_next_to_the_week_and_the_edits_stay()
    {
        var vm = await LoadedAsync();
        _client.Reply = m =>
        {
            if (m is ReplaceSettings) _client.BumpVersion();
            return null;
        };
        vm.Week!.Days[0].LimitText = "90";

        await vm.Week.SaveCommand.ExecuteAsync(null);

        Assert.Equal(FamilyContext.UpdateAttempts, _client.SentOf<ReplaceSettings>().Count());
        Assert.Equal(Strings.Error_Conflict, vm.Week.Message);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("90", vm.Week.Days[0].LimitText);
        Assert.True(vm.Week.IsDirty);
    }

    [Fact]
    public async Task Unsaved_edits_in_one_section_survive_saving_another()
    {
        var vm = await LoadedAsync();
        var week = vm.Week!;
        week.Days[2].LimitText = "45";
        vm.Profile!.YouTubeMode = "off";

        vm.NewChannel = "UCnature0000000000000004";
        await vm.AddChannelCommand.ExecuteAsync(null);

        Assert.Same(week, vm.Week);
        Assert.Equal("45", vm.Week!.Days[2].LimitText);
        Assert.True(vm.Week.IsDirty);
        Assert.Equal("off", vm.Profile!.YouTubeMode);
        Assert.Equal(3, vm.Channels.Count);
    }

    // ---- profile ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_profile_sends_only_the_changed_fields()
    {
        var vm = await LoadedAsync();
        var profile = vm.Profile!;
        Assert.False(profile.SaveCommand.CanExecute(null));
        profile.DisplayName = " נועי ";
        profile.FilterMode = "blocklist";
        profile.AgeText = "9";

        await profile.SaveCommand.ExecuteAsync(null);

        Assert.Equal(new SetMember("noa", DisplayName: "נועי", Age: 9, FilterMode: "blocklist"), _client.Single<SetMember>());
        Assert.Equal("ההגדרות של נועי נשמרו.", vm.StatusMessage);
        Assert.False(vm.Profile!.IsDirty);
    }

    [Theory]
    [InlineData(" ", "8", "כתבו שם לכל בן משפחה.")]
    [InlineData("יואב", "8", "כבר יש בן משפחה בשם יואב.")]
    [InlineData("נועה", "0", "גיל הוא מספר בין 1 ל-120, או השאירו ריק.")]
    [InlineData("נועה", "שמונה", "גיל הוא מספר בין 1 ל-120, או השאירו ריק.")]
    public async Task A_profile_that_does_not_make_sense_is_explained_and_not_sent(string name, string age, string message)
    {
        var vm = await LoadedAsync();
        vm.Profile!.DisplayName = name;
        vm.Profile.AgeText = age;
        vm.Profile.Role = "teen";

        await vm.Profile.SaveCommand.ExecuteAsync(null);

        Assert.Equal(message, vm.Profile.Message);
        Assert.Empty(_client.SentOf<SetMember>());
    }

    [Fact]
    public async Task Clearing_the_age_goes_through_the_settings()
    {
        var vm = await LoadedAsync();
        vm.Profile!.AgeText = "";

        await vm.Profile.SaveCommand.ExecuteAsync(null);

        Assert.Empty(_client.SentOf<SetMember>());
        Assert.Null(LastReplace.Settings.Members.Single(m => m.Id == "noa").Age);
        Assert.Equal(TestData.Family().Members.Where(m => m.Id != "noa"), LastReplace.Settings.Members.Where(m => m.Id != "noa"));
    }

    // ---- site rules ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_personal_rule_is_checked_and_added_for_this_member_with_its_expiry()
    {
        var vm = await LoadedAsync("yoav");
        vm.NewPattern = "*.Games.example";
        vm.NewType = "wildcard";
        vm.NewAction = "block";
        vm.NewExpiry = RuleExpiry.Today;

        await vm.AddRuleCommand.ExecuteAsync(null);

        Assert.Equal(new AddRule(new RuleDto("r-test", "yoav", "*.games.example", "wildcard", "block", ExpiresAt: new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.FromHours(3)))),
            _client.Single<AddRule>());
        Assert.Equal("", vm.NewPattern);
        Assert.Equal(RuleExpiry.Permanent, vm.NewExpiry);
    }

    [Fact]
    public async Task An_invalid_pattern_is_explained_next_to_the_form()
    {
        var vm = await LoadedAsync();
        vm.NewPattern = "example.com/games";

        await vm.AddRuleCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Validation_DomainHasPath, vm.RuleMessage);
        Assert.Empty(_client.SentOf<AddRule>());
        vm.NewPattern = "example.com";
        Assert.Null(vm.RuleMessage);
    }

    [Fact]
    public async Task A_rule_the_service_refuses_is_shown_next_to_the_form_and_the_form_is_kept()
    {
        var vm = await LoadedAsync();
        _client.Reply = m => m is AddRule ? new AdminError("bad_request", "Rule 'r-test': the regular expression is too long for the browser.") : null;
        vm.NewPattern = "^https?://games\\.example/";
        vm.NewType = "regex";

        await vm.AddRuleCommand.ExecuteAsync(null);

        Assert.Equal("שירות הסינון לא ביצע את הפעולה: Rule 'r-test': the regular expression is too long for the browser.", vm.RuleMessage);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("^https?://games\\.example/", vm.NewPattern);
    }

    [Fact]
    public async Task Removing_a_personal_rule_asks_first()
    {
        _client.Settings = _client.Settings with { Rules = [.. _client.Settings.Rules, new RuleDto("r-yt", "yoav", "youtube.com", "domain", "block")] };
        var vm = await LoadedAsync("yoav");
        var rule = Assert.Single(vm.Rules);

        _dialogs.Answer = false;
        await vm.RemoveRuleCommand.ExecuteAsync(rule);
        Assert.Empty(_client.SentOf<RemoveRule>());

        _dialogs.Answer = true;
        await vm.RemoveRuleCommand.ExecuteAsync(rule);
        Assert.Equal(new RemoveRule("r-yt"), _client.Single<RemoveRule>());
    }

    // ---- channels -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_channel_is_added_from_its_address_with_a_handle_and_name()
    {
        var vm = await LoadedAsync();
        vm.NewChannel = "https://www.youtube.com/channel/UCnature0000000000000004";
        vm.NewChannelHandle = "NatureKids";
        vm.NewChannelTitle = " Nature Kids ";

        await vm.AddChannelCommand.ExecuteAsync(null);

        var channel = LastReplace.Settings.YouTubeChannels.Last();
        Assert.Equal(new YouTubeChannelDto("UCnature0000000000000004", "@naturekids", "Nature Kids", "noa", "allow"), channel);
        Assert.Equal(TestData.Family().YouTubeChannels, LastReplace.Settings.YouTubeChannels.SkipLast(1));
        Assert.Equal("הערוץ Nature Kids נוסף.", Bidi.Plain(vm.StatusMessage));
        Assert.Equal("", vm.NewChannel);
        Assert.Contains(vm.Channels, c => c.ChannelId == "UCnature0000000000000004");
    }

    [Theory]
    [InlineData("@naturekids", "", nameof(Strings.Validation_ChannelHandleOnly))]
    [InlineData("UCshort", "", nameof(Strings.Validation_ChannelId))]
    [InlineData("UCnature0000000000000004", "a b", nameof(Strings.Validation_ChannelHandle))]
    [InlineData("UCkhanKids00000000000001", "", nameof(Strings.Validation_Duplicate))]
    public async Task A_channel_that_cannot_be_used_is_explained(string channel, string handle, string messageKey)
    {
        var vm = await LoadedAsync();
        vm.NewChannel = channel;
        vm.NewChannelHandle = handle;

        await vm.AddChannelCommand.ExecuteAsync(null);

        Assert.Equal(Strings.ResourceManager.GetString(messageKey, Strings.Culture), vm.ChannelMessage);
        Assert.Empty(_client.SentOf<ReplaceSettings>());
    }

    [Fact]
    public async Task Blocking_an_allowed_channel_replaces_its_entry()
    {
        var vm = await LoadedAsync();
        vm.NewChannel = "UCkhanKids00000000000001";
        vm.NewChannelAction = "block";

        await vm.AddChannelCommand.ExecuteAsync(null);

        var noa = LastReplace.Settings.YouTubeChannels.Where(c => c.Member == "noa" && c.ChannelId == "UCkhanKids00000000000001").ToList();
        Assert.Equal("block", Assert.Single(noa).Action);
    }

    [Fact]
    public async Task Removing_a_channel_asks_first()
    {
        var vm = await LoadedAsync();
        var sesame = vm.Channels.Single(c => c.ChannelId == "UCsesame0000000000000002");

        _dialogs.Answer = false;
        await vm.RemoveChannelCommand.ExecuteAsync(sesame);
        Assert.Empty(_client.SentOf<ReplaceSettings>());

        _dialogs.Answer = true;
        await vm.RemoveChannelCommand.ExecuteAsync(sesame);
        Assert.Equal("להסיר את הערוץ Sesame (sim)?", Bidi.Plain(_dialogs.Asked[^1]));
        Assert.DoesNotContain(LastReplace.Settings.YouTubeChannels, c => c.ChannelId == "UCsesame0000000000000002");
        Assert.Equal(2, LastReplace.Settings.YouTubeChannels.Count);
    }

    // ---- apps ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_program_is_approved_for_this_member()
    {
        var vm = await LoadedAsync();
        vm.NewAppValue = @"C:\Games\Roblox\RobloxPlayer.exe";
        vm.NewAppAlwaysAllowed = true;
        vm.NewAppExpiry = RuleExpiry.Hour;

        await vm.AddAppCommand.ExecuteAsync(null);

        Assert.Equal(new AppRuleDto("a-test", "noa", "path", @"C:\Games\Roblox\RobloxPlayer.exe", "allow", true, TestData.Now.AddHours(1)),
            LastReplace.Settings.AppRules.Last());
        Assert.Equal(TestData.Family().AppRules, LastReplace.Settings.AppRules.SkipLast(1));
        Assert.Equal("RobloxPlayer.exe אושרה.", Bidi.Plain(vm.StatusMessage));
        Assert.Equal("", vm.NewAppValue);
        Assert.False(vm.NewAppAlwaysAllowed);
    }

    [Fact]
    public async Task A_store_app_is_approved_by_its_family_name()
    {
        var vm = await LoadedAsync();
        vm.NewAppMatch = "package";
        vm.NewAppValue = "Microsoft.MinecraftEducationEdition_1.20.0.0_x64__8wekyb3d8bbwe";

        await vm.AddAppCommand.ExecuteAsync(null);

        var app = LastReplace.Settings.AppRules.Last();
        Assert.Equal(("package", "Microsoft.MinecraftEducationEdition_8wekyb3d8bbwe", null), (app.Match, app.Value, app.ExpiresAt));
    }

    [Theory]
    [InlineData("path", "roblox.exe", nameof(Strings.Validation_AppPathForm))]
    [InlineData("path", @"C:\Games\minecraft\MINECRAFT.EXE", nameof(Strings.Validation_Duplicate))]
    [InlineData("package", "minecraft", nameof(Strings.Validation_AppPackageForm))]
    public async Task An_app_that_cannot_be_used_is_explained(string match, string value, string messageKey)
    {
        var vm = await LoadedAsync("yoav");
        vm.NewAppMatch = match;
        vm.NewAppValue = value;

        await vm.AddAppCommand.ExecuteAsync(null);

        Assert.Equal(Strings.ResourceManager.GetString(messageKey, Strings.Culture), vm.AppMessage);
        Assert.Empty(_client.SentOf<ReplaceSettings>());
    }

    [Fact]
    public async Task Removing_an_app_asks_first_and_removes_only_it()
    {
        var vm = await LoadedAsync("yoav");
        var minecraft = vm.Apps.Single(a => a.Id == "a-minecraft");

        await vm.RemoveAppCommand.ExecuteAsync(minecraft);

        Assert.Equal("להסיר את האישור של minecraft.exe?", Bidi.Plain(Assert.Single(_dialogs.Asked)));
        Assert.Equal(["a-school", "a-mc-store"], LastReplace.Settings.AppRules.Select(a => a.Id));
    }

    // ---- categories ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Category_choices_replace_only_this_members_choices()
    {
        var vm = await LoadedAsync();
        var categories = vm.Categories!;
        categories.Items.Single(c => c.Name == "social").Choice = "block";
        categories.Items.Single(c => c.Name == "educational").Choice = CategoryChoiceViewModel.Default;
        Assert.True(categories.IsDirty);

        await categories.SaveCommand.ExecuteAsync(null);

        Assert.Equal(
            [new MemberCategoryDto("yoav", "social", "block"), new MemberCategoryDto("yoav", "gaming", "block"), new MemberCategoryDto("noa", "social", "block")],
            LastReplace.Settings.MemberCategories);
        Assert.Equal("הקטגוריות של נועה נשמרו.", vm.StatusMessage);
        Assert.False(vm.Categories!.IsDirty);
    }

    [Fact]
    public async Task Reverting_categories_restores_the_stored_choices()
    {
        var vm = await LoadedAsync();
        vm.Categories!.Items.Single(c => c.Name == "gaming").Choice = "allow";

        vm.Categories.RevertCommand.Execute(null);

        Assert.Equal(CategoryChoiceViewModel.Default, vm.Categories.Items.Single(c => c.Name == "gaming").Choice);
        Assert.False(vm.Categories.IsDirty);
    }

    // ---- agent computer -----------------------------------------------------------------------------------------

    [Fact]
    public async Task On_an_agent_computer_the_page_says_where_members_are_managed()
    {
        _client.Reply = m => m switch
        {
            GetStatus => _client.Status with { IsHub = false },
            GetSettings => new AdminError("not_hub", "This computer is managed by the hub computer; make this change there."),
            _ => null,
        };

        var vm = await LoadedAsync();

        Assert.True(vm.IsNotHub);
        Assert.False(vm.HasMember);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(vm.Week);
    }

    private static IEnumerable<string> Windows(DayEditor day) => day.Windows.Select(w => $"{w.Start}-{w.End}");

    private static string Text(ScheduleDto s) => $"{s.Member}:{string.Join(",", s.Days)}:{s.Start}-{s.End}";

    private static string Text(DailyLimitDto l) => $"{l.Member}:{string.Join(",", l.Days)}:{l.Minutes}";
}

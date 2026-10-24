using WebFilter.Admin.Resources;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.Tests;

/// <summary>
/// "A new Windows account was found" on the dashboard and the devices page: which accounts get the card, and the settings
/// each of its four answers saves (an existing member, a new child from the wizard's template, an adult without
/// filtering, or keeping it blocked as a guest).
/// </summary>
public sealed class NewAccountCardTests
{
    private const string VisitorId = "S-1-5-21-2000-1005";

    private static readonly string[] TeenLists = ["adult", "gambling", "proxies", "unfiltered-search", "ads-popups"];

    private readonly FakeAdminClient _client = new();
    private readonly FamilyContext _context;

    public NewAccountCardTests()
    {
        _client.Devices.AddRange(TestData.Devices());
        _context = new FamilyContext(_client, new ManualTime(TestData.Now)) { NewMemberId = () => "m-new" };
    }

    private async Task<DashboardViewModel> DashboardAsync()
    {
        var vm = new DashboardViewModel(_context);
        await vm.RefreshAsync();
        return vm;
    }

    private async Task<DevicesViewModel> DevicesAsync()
    {
        var vm = new DevicesViewModel(_context, new FakeDialogs(), new FakeClipboard(), new ManualTimer());
        await vm.RefreshAsync();
        return vm;
    }

    private async Task<NewAccountViewModel> VisitorCardAsync() => Assert.Single((await DashboardAsync()).NewAccounts);

    private static DeviceAccountItemViewModel Account(DevicesViewModel vm, string device, string name) =>
        vm.AccountGroups.Single(g => g.DeviceId == device).Accounts.Single(a => a.Name == name);

    private FamilyBundle Saved => _client.SentOf<ReplaceSettings>().Last().Settings;

    private static AccountDto Visitor(FamilyBundle settings) => settings.Devices.Single(d => d.Id == "pc-kids").Accounts.Single(a => a.AccountId == VisitorId);

    private void AddAccounts(string deviceId, params AccountDto[] accounts) =>
        _client.Settings = _client.Settings with
        {
            Devices = [.. _client.Settings.Devices.Select(d => d.Id == deviceId ? d with { Accounts = [.. d.Accounts, .. accounts] } : d)],
        };

    [Fact]
    public async Task Only_standard_accounts_nobody_answered_for_get_the_card_this_computer_first()
    {
        AddAccounts("pc-parents", new AccountDto("S-1-5-21-1000-1005", "yinon", null, false), new AccountDto("S-1-5-21-1000-1006", "admin2", null, true));
        AddAccounts("pc-kids", new AccountDto("S-1-5-21-2000-1010", "cousin", null, false, Guest: true));
        // A revoked computer and one not paired here: their accounts wait for nobody.
        AddAccounts("laptop-yoav", new AccountDto("S-1-5-21-3000-1009", "friend", null, false));
        _client.Settings = _client.Settings with
        {
            Devices = [.. _client.Settings.Devices, new DeviceDto("pc-old", "windows", "agent", [new AccountDto("S-1-5-21-4000-1001", "old-kid", null, false)])],
        };

        var dashboard = await DashboardAsync();

        Assert.True(dashboard.HasNewAccounts);
        Assert.Equal(["נמצא חשבון Windows חדש: yinon (במחשב הזה)", "נמצא חשבון Windows חדש: visitor (במחשב KIDS-PC)"],
            dashboard.NewAccounts.Select(c => Bidi.Plain(c.Title)));
        Assert.All(dashboard.NewAccounts, c => Assert.Equal((NewAccountAnswer.None, false, false), (c.Answer, c.CanClose, c.IsGuest)));
        Assert.Equal(2, _context.UnlinkedAccountCount);

        var devices = await DevicesAsync();
        Assert.Equal("2", devices.Badge);
        Assert.Equal(["yinon"], devices.AccountGroups[0].Accounts.Where(a => a.ShowsCard).Select(a => a.Name));
        Assert.Equal(["visitor"], devices.AccountGroups[1].Accounts.Where(a => a.ShowsCard).Select(a => a.Name));
        Assert.Equal((false, true), (Account(devices, "pc-kids", "cousin").ShowsCard, Account(devices, "pc-kids", "cousin").ShowsGuest));
        Assert.Null(Account(devices, "pc-parents", "admin2").Card);
        Assert.False(Account(devices, "pc-parents", "yinon").Card!.CanClose);
        Assert.Equal("נמצא חשבון Windows חדש: yinon (במחשב הזה)", Bidi.Plain(Account(devices, "pc-parents", "yinon").Card!.Title));
    }

    [Fact]
    public async Task An_existing_member_children_teens_or_adults_gets_the_account()
    {
        var dashboard = await DashboardAsync();
        var card = Assert.Single(dashboard.NewAccounts);
        Assert.Equal(["נועה", "יואב", "דנה", "סבתא"], card.MemberOptions.Select(o => o.Label));

        card.ChooseExistingCommand.Execute(null);
        Assert.True(card.IsChoosingExisting);
        Assert.False(card.LinkExistingCommand.CanExecute(null));
        card.ExistingMemberId = "yoav";
        await card.LinkExistingCommand.ExecuteAsync(null);

        var replace = _client.Single<ReplaceSettings>();
        Assert.Equal(7, replace.IfVersion);
        Assert.Equal(new AccountDto(VisitorId, "visitor", "yoav", false), Visitor(replace.Settings));
        Assert.Equal(TestData.Family().Members, replace.Settings.Members);
        Assert.Equal("החשבון visitor ב-KIDS-PC קושר ליואב.", Bidi.Plain(dashboard.StatusMessage));
        Assert.Empty(dashboard.NewAccounts);
        Assert.False(dashboard.HasNewAccounts);
    }

    /// <summary>The wizard's child template (MVP-PLAN §4.5, PROTECTION-SPEC §1, §3, §11, §12), with the account linked to the new member.</summary>
    [Fact]
    public async Task A_new_child_starts_from_the_wizards_child_template_and_gets_the_account()
    {
        var devices = await DevicesAsync();
        var card = Account(devices, "pc-kids", "visitor").Card!;

        card.ChooseNewChildCommand.Execute(null);
        Assert.True(card.IsAddingChild);
        Assert.Equal(("visitor", "child", ""), (card.ChildName, card.ChildRole, card.ChildAgeText));
        Assert.Equal("רק אתרים מאושרים · YouTube: רק ערוצים מאושרים · 15:00–19:00, עד 120 דקות ביום"
                     + " · חסימת חיפוש תמונות, חסימת קפיצות לאתרים אחרים, חסימת YouTube Shorts, חסימת פרסומות · לימודים: מותרת תמיד, כלי בית ספר: מותרת תמיד",
            Bidi.Plain(card.ChildTemplateText));
        card.ChildName = "ינון";
        card.ChildAgeText = "9";
        await card.AddChildCommand.ExecuteAsync(null);

        var saved = Saved;
        Assert.Equal(new MemberDto("m-new", "ינון", "child", 9, "allowlist", "allowlist", BlockImageSearch: true, JumpGuard: true, BlockYouTubeShorts: true, LogSearches: false, BlockAds: true),
            saved.Members[^1]);
        Assert.Equal(TestData.Family().Members, saved.Members.SkipLast(1));
        Assert.Equal("m-new", Visitor(saved).Member);
        Assert.Equal(["0,1,2,3,4,5,6:15:00-19:00"], saved.Schedules.Where(s => s.Member == "m-new").Select(s => $"{string.Join(",", s.Days)}:{s.Start}-{s.End}"));
        Assert.Equal(["0,1,2,3,4,5,6:120"], saved.DailyLimits.Where(l => l.Member == "m-new").Select(l => $"{string.Join(",", l.Days)}:{l.Minutes}"));
        Assert.Equal([("educational", "allow"), ("school-tools", "allow")], saved.MemberCategories.Where(c => c.Member == "m-new").Select(c => (c.Category, c.Action)));
        // The family's own learning sites stay; the school tools list, which it did not have, is copied in, with the sign-in rules.
        Assert.Equal(TestData.Family().Categories["educational"], saved.Categories["educational"]);
        Assert.Contains("classroom.google.com", saved.Categories["school-tools"]);
        Assert.Equal(SchoolSignIn.Rules, saved.Rules.Where(SchoolSignIn.IsOurs));

        Assert.Equal("ינון נוסף/ה לבני המשפחה, והחשבון visitor ב-KIDS-PC שויך לינון.", Bidi.Plain(devices.StatusMessage));
        var row = Account(devices, "pc-kids", "visitor");
        Assert.Equal(("m-new", true, false, null), (row.MemberId, row.ShowsChoice, row.ShowsCard, row.Card));
        Assert.Null(devices.Badge);
    }

    [Fact]
    public async Task A_new_teen_starts_from_the_teen_template()
    {
        var card = await VisitorCardAsync();

        card.ChooseNewChildCommand.Execute(null);
        card.ChildRole = "teen";
        Assert.StartsWith("חסימת אתרים לא מתאימים · YouTube: מצב מוגבל · 14:00–21:30, עד 240 דקות ביום · חסימת קפיצות לאתרים אחרים, חסימת פרסומות · ", Bidi.Plain(card.ChildTemplateText));
        await card.AddChildCommand.ExecuteAsync(null);

        var saved = Saved;
        Assert.Equal(new MemberDto("m-new", "visitor", "teen", null, "blocklist", "restricted", BlockImageSearch: false, JumpGuard: true, BlockYouTubeShorts: false, LogSearches: false, BlockAds: true, YouTubeChannelSearch: true),
            saved.Members[^1]);
        Assert.Equal(["14:00-21:30"], saved.Schedules.Where(s => s.Member == "m-new").Select(s => $"{s.Start}-{s.End}"));
        Assert.Equal([240], saved.DailyLimits.Where(l => l.Member == "m-new").Select(l => l.Minutes));
        Assert.Equal(TeenLists.Select(l => (l, "block")), saved.MemberCategories.Where(c => c.Member == "m-new").Select(c => (c.Category, c.Action)));
        Assert.All(TeenLists, l => Assert.NotEmpty(saved.Categories[l]));
        Assert.False(saved.Categories.ContainsKey("school-tools"));
        Assert.DoesNotContain(saved.Rules, SchoolSignIn.IsOurs);
        Assert.Equal("m-new", Visitor(saved).Member);
    }

    [Fact]
    public async Task The_new_members_name_and_age_are_checked_before_anything_is_saved()
    {
        var card = await VisitorCardAsync();
        card.ChooseNewChildCommand.Execute(null);

        card.ChildName = " נועה ";
        await card.AddChildCommand.ExecuteAsync(null);
        Assert.Equal("כבר יש בן משפחה בשם נועה.", card.Message);

        card.ChildName = "ינון";
        Assert.Null(card.Message);
        card.ChildAgeText = "200";
        await card.AddChildCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Validation_Age, card.Message);

        card.ChooseAdultCommand.Execute(null);
        Assert.Null(card.Message);
        card.AdultName = " ";
        await card.LinkAdultCommand.ExecuteAsync(null);
        Assert.Equal(Strings.Validation_NameEmpty, card.Message);

        Assert.Empty(_client.SentOf<ReplaceSettings>());
    }

    [Fact]
    public async Task A_new_adult_is_added_without_filtering_and_gets_the_account()
    {
        var card = await VisitorCardAsync();

        card.ChooseAdultCommand.Execute(null);
        Assert.True(card.IsChoosingAdult);
        Assert.Equal(["דנה", "סבתא", "מבוגר/ת חדש/ה"], card.AdultOptions.Select(o => o.Label));
        Assert.Equal((true, true, "visitor", "הוספה ושיוך"), (card.HasAdults, card.IsNewAdult, card.AdultName, card.AdultActionText));
        card.AdultName = "אמא";
        await card.LinkAdultCommand.ExecuteAsync(null);

        var saved = Saved;
        Assert.Equal(new MemberDto("m-new", "אמא", "adult", null, "unrestricted", "unrestricted"), saved.Members[^1]);
        Assert.Equal("m-new", Visitor(saved).Member);
        Assert.DoesNotContain(saved.Schedules, s => s.Member == "m-new");
        Assert.DoesNotContain(saved.DailyLimits, l => l.Member == "m-new");
        Assert.DoesNotContain(saved.MemberCategories, c => c.Member == "m-new");
        Assert.Equal(TestData.Family().Rules, saved.Rules);
        Assert.Equal(TestData.Family().Categories.Keys, saved.Categories.Keys);
    }

    [Fact]
    public async Task An_existing_adult_can_get_the_account_instead()
    {
        var card = await VisitorCardAsync();

        card.ChooseAdultCommand.Execute(null);
        card.AdultChoice = "savta";
        Assert.Equal((false, "שיוך"), (card.IsNewAdult, card.AdultActionText));
        await card.LinkAdultCommand.ExecuteAsync(null);

        Assert.Equal("savta", Visitor(Saved).Member);
        Assert.Equal(TestData.Family().Members, Saved.Members);
    }

    [Fact]
    public async Task Keeping_it_blocked_as_a_guest_is_saved_and_can_be_changed_later()
    {
        var devices = await DevicesAsync();
        var row = Account(devices, "pc-kids", "visitor");

        await row.Card!.KeepAsGuestCommand.ExecuteAsync(null);

        Assert.Equal(new AccountDto(VisitorId, "visitor", null, false, Guest: true), Visitor(Saved));
        Assert.Equal("החשבון visitor ב-KIDS-PC נשאר חסום, כחשבון אורח.", Bidi.Plain(devices.StatusMessage));
        Assert.Equal((false, true, false), (row.ShowsCard, row.ShowsGuest, row.ShowsChoice));
        Assert.Null(devices.Badge);
        Assert.Equal(0, _context.UnlinkedAccountCount);
        Assert.Empty((await DashboardAsync()).NewAccounts);

        // "Change…" shows the card again; closing it (or keeping the guest) saves nothing.
        row.ChangeGuestCommand.Execute(null);
        Assert.Equal((true, false), (row.ShowsCard, row.ShowsGuest));
        Assert.True(row.Card!.CanClose);
        Assert.True(row.Card.IsGuest);
        row.Card.CloseCommand.Execute(null);
        Assert.Equal((false, true), (row.ShowsCard, row.ShowsGuest));
        row.ChangeGuestCommand.Execute(null);
        await row.Card.KeepAsGuestCommand.ExecuteAsync(null);
        Assert.Equal((false, true), (row.ShowsCard, row.ShowsGuest));
        Assert.Single(_client.SentOf<ReplaceSettings>());

        // Linked later: no longer a guest.
        row.ChangeGuestCommand.Execute(null);
        row.Card.ChooseExistingCommand.Execute(null);
        row.Card.ExistingMemberId = "yoav";
        await row.Card.LinkExistingCommand.ExecuteAsync(null);
        Assert.Equal(new AccountDto(VisitorId, "visitor", "yoav", false), Visitor(Saved));
        Assert.Equal((true, false, false, null), (row.ShowsChoice, row.ShowsGuest, row.ShowsCard, row.Card));
    }

    [Fact]
    public async Task A_refusal_is_shown_on_the_card()
    {
        var dashboard = await DashboardAsync();
        var card = Assert.Single(dashboard.NewAccounts);
        _client.Reply = m => m is ReplaceSettings ? new AdminError("bad_request", "database is locked") : null;

        await card.KeepAsGuestCommand.ExecuteAsync(null);

        Assert.Equal("שירות הסינון לא ביצע את הפעולה: database is locked", card.Message);
        Assert.Null(dashboard.ErrorMessage);
        Assert.Null(dashboard.StatusMessage);
        Assert.Same(card, Assert.Single(dashboard.NewAccounts));
    }

    [Fact]
    public async Task A_new_member_keeps_its_id_when_the_save_is_applied_again_after_a_conflict()
    {
        var ids = 0;
        _context.NewMemberId = () => $"m-{++ids}";
        var replies = 0;
        _client.Reply = m => m is ReplaceSettings && ++replies == 1 ? new AdminError(ReplaceSettings.ConflictCode, "changed meanwhile") : null;
        var card = await VisitorCardAsync();

        card.ChooseNewChildCommand.Execute(null);
        await card.AddChildCommand.ExecuteAsync(null);

        Assert.Equal(["m-1", "m-1"], _client.SentOf<ReplaceSettings>().Select(r => r.Settings.Members[^1].Id));
        Assert.Equal("m-1", Visitor(Saved).Member);
    }

    [Fact]
    public async Task A_refresh_keeps_the_open_answer_and_what_the_parent_typed()
    {
        var dashboard = await DashboardAsync();
        var card = Assert.Single(dashboard.NewAccounts);
        card.ChooseNewChildCommand.Execute(null);
        card.ChildName = "ינון";
        card.ChildRole = "teen";
        _client.BumpVersion(_client.Settings with { Members = [.. _client.Settings.Members.Select(m => m.Id == "noa" ? m with { DisplayName = "נועי" } : m)] });

        await dashboard.RefreshAsync();

        Assert.Same(card, Assert.Single(dashboard.NewAccounts));
        Assert.Equal((NewAccountAnswer.NewChild, "ינון", "teen"), (card.Answer, card.ChildName, card.ChildRole));
        Assert.Contains(card.MemberOptions, o => o.Label == "נועי");
        Assert.Empty(_client.SentOf<ReplaceSettings>());
    }
}

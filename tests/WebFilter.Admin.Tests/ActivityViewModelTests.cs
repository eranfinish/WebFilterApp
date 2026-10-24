using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Tests;

public sealed class ActivityViewModelTests
{
    private readonly FakeAdminClient _client = new();

    private ActivityViewModel Create() => new(new FamilyContext(_client, new ManualTime(TestData.Now)));

    private GetEvents LastQuery => _client.SentOf<GetEvents>().Last();

    [Fact]
    public async Task Loads_the_last_24_hours_for_everyone_by_default()
    {
        var vm = Create();
        await vm.RefreshAsync();

        Assert.Equal(new GetEvents(TestData.Now.AddHours(-24), null, null, ActivityViewModel.MaxEvents), LastQuery);
        Assert.Equal(["", "noa", "yoav", "dana", "savta"], vm.MemberOptions.Select(o => o.Value));
        Assert.Equal("הכל", vm.MemberOptions[0].Label);
        Assert.Equal(11, vm.TypeOptions.Count); // "all" and the ten event types (search and override: PROTECTION-SPEC §6)
        Assert.True(vm.HasNoEvents);
    }

    [Fact]
    public async Task Changing_a_filter_reloads_with_it()
    {
        var vm = Create();
        await vm.RefreshAsync();

        vm.SelectedType = "blocked";
        Assert.Equal(new GetEvents(TestData.Now.AddHours(-24), "blocked", null, 500), LastQuery);

        vm.SelectedMember = "noa";
        Assert.Equal(new GetEvents(TestData.Now.AddHours(-24), "blocked", "noa", 500), LastQuery);

        vm.SelectedPeriod = ActivityViewModel.LastWeek;
        Assert.Equal(new GetEvents(TestData.Now.AddDays(-7), "blocked", "noa", 500), LastQuery);

        vm.SelectedType = ActivityViewModel.AnyValue;
        vm.SelectedMember = null;
        Assert.Equal(new GetEvents(TestData.Now.AddDays(-7), null, null, 500), LastQuery);
        Assert.Equal(6, _client.SentOf<GetEvents>().Count());
    }

    [Fact]
    public void Filters_set_before_the_first_load_wait_for_it()
    {
        var vm = Create();

        vm.SelectedType = "tamper";

        Assert.Empty(_client.Sent);
        Assert.Equal("tamper", vm.BuildQuery().EventType);
    }

    [Fact]
    public async Task Events_are_shown_newest_first_in_plain_words()
    {
        _client.Events.AddRange(
        [
            new EventDto("blocked", TestData.Now.AddMinutes(-50), "noa", "pc-kids", "blocked_not_in_allowlist", "https://games.example/", null, null, null),
            new EventDto("time_warning", TestData.Now.AddMinutes(-40), "yoav", "pc-kids", null, null, null, 5, null),
            new EventDto("admin_change", TestData.Now.AddMinutes(-30), "noa", null, null, null, null, null, "extend_time"),
            new EventDto("access_request", TestData.Now.AddMinutes(-20), "noa", "pc-kids", null, "https://games.example/", null, null, "url"),
            new EventDto("app_blocked", TestData.Now.AddMinutes(-10), "yoav", "laptop-yoav", "blocked_app_network", null, @"C:\Games\x.exe", null, null),
            new EventDto("service", TestData.Now.AddMinutes(-5), null, "pc-kids", "some_new_code", null, null, null, "restarted"),
        ]);
        var vm = Create();
        await vm.RefreshAsync();

        Assert.Equal(["service", "app_blocked", "access_request", "admin_change", "time_warning", "blocked"], vm.Events.Select(e => e.Type));
        var blocked = vm.Events[5];
        Assert.Equal("19.10 15:10", blocked.TimeText);
        Assert.Equal("נועה", blocked.MemberName);
        Assert.Equal("אתר נחסם", blocked.TypeText);
        Assert.Equal("https://games.example/ — לא ברשימת האתרים המאושרים", Bidi.Plain(blocked.Details));
        Assert.Equal("pc-kids", blocked.DeviceText);
        Assert.True(blocked.IsAlert);

        Assert.Equal("נותרו 5 דקות", vm.Events[4].Details);
        Assert.Equal("שינוי של הורה", vm.Events[3].TypeText);
        Assert.Equal("נוסף זמן גלישה", vm.Events[3].Details);
        Assert.False(vm.Events[3].IsAlert);
        Assert.Equal("אתר: https://games.example/", Bidi.Plain(vm.Events[2].Details));
        Assert.Equal(@"C:\Games\x.exe — אפליקציה בלי אישור לרשת", Bidi.Plain(vm.Events[1].Details));
        Assert.Equal("restarted — some_new_code", vm.Events[0].Details);
        Assert.Equal("", vm.Events[0].MemberName);
        Assert.Equal("6 אירועים", vm.CountText);
        Assert.False(vm.HasNoEvents);
    }

    [Fact]
    public async Task Searches_have_a_filter_of_their_own()
    {
        var vm = Create();
        await vm.RefreshAsync();

        Assert.Contains(new Option("search", "חיפושים"), vm.TypeOptions);
        vm.SelectedType = "search";

        Assert.Equal("search", LastQuery.EventType);
    }

    [Fact]
    public async Task Searches_overrides_and_repairs_are_shown_in_plain_words()
    {
        _client.Events.AddRange(
        [
            new EventDto("search", TestData.Now.AddMinutes(-50), "yoav", "laptop-yoav", null, "https://www.google.com/search?q=lego+star+wars", null, null, "google:lego star wars"),
            new EventDto("search", TestData.Now.AddMinutes(-45), "noa", "pc-kids", null, "https://www.youtube.com/results?search_query=x", null, null, "youtube:שיר ערש: גרסה 2"),
            new EventDto("override", TestData.Now.AddMinutes(-40), "yoav", "laptop-yoav", "blocked_jump", "https://news.example/story", null, null, null),
            new EventDto("tamper", TestData.Now.AddMinutes(-30), "noa", "pc-kids", null, null, null, null, "policy_repaired"),
            new EventDto("tamper", TestData.Now.AddMinutes(-20), "yoav", "laptop-yoav", null, null, null, null, "network_repaired"),
            new EventDto("tamper", TestData.Now.AddMinutes(-10), null, "pc-kids", null, null, null, null, "unclean_stop"),
            new EventDto("blocked", TestData.Now.AddMinutes(-5), "noa", "pc-kids", "blocked_image_search", "https://www.google.com/search?q=cats&udm=2", null, null, null),
        ]);
        var vm = Create();
        await vm.RefreshAsync();
        var events = vm.Events.Reverse().ToList();

        Assert.Equal("חיפוש", events[0].TypeText);
        Assert.Equal("חיפוש ב-Google: lego star wars — https://www.google.com/search?q=lego+star+wars", Bidi.Plain(events[0].Details));
        // A query in Hebrew stays as typed, colons included (only the first ':' separates the engine).
        Assert.StartsWith("חיפוש ב-YouTube: שיר ערש: גרסה 2 — ", events[1].Details, StringComparison.Ordinal);
        Assert.False(events[0].IsAlert);

        Assert.Equal("נפתח למרות חסימה", events[2].TypeText);
        Assert.Equal("נפתח בכל זאת: https://news.example/story — האתר ניסה לפתוח אתר אחר", Bidi.Plain(events[2].Details));

        Assert.Equal("הגדרות שהסינון מנהל (מדיניות של הדפדפנים או של Windows) שונו, והסינון החזיר אותן", events[3].Details);
        Assert.Equal("מסנני הרשת של החשבון הוסרו, והסינון החזיר אותם", events[4].Details);
        // Not worded as an attack: a power cut looks the same.
        Assert.Contains("הפסקת חשמל", events[5].Details, StringComparison.Ordinal);
        Assert.Contains("לא בהכרח ניסיון לעקוף", events[5].Details, StringComparison.Ordinal);
        Assert.All(events.Skip(3).Take(3), e => Assert.True(e.IsAlert));

        Assert.Equal("https://www.google.com/search?q=cats&udm=2 — חיפוש תמונות חסום", Bidi.Plain(events[6].Details));
    }

    /// <summary>PROTECTION-SPEC §14: a browser cut off for lack of the extension names the browser and the member.</summary>
    [Fact]
    public async Task A_browser_blocked_without_the_extension_names_the_browser_and_the_member()
    {
        _client.Events.AddRange(
        [
            new EventDto("tamper", TestData.Now.AddMinutes(-30), "noa", "pc-kids", null, null, null, null, "extension_missing:chrome"),
            new EventDto("tamper", TestData.Now.AddMinutes(-20), null, "pc-kids", null, null, null, null, "extension_missing:edge"),
        ]);
        var vm = Create();
        await vm.RefreshAsync();
        var events = vm.Events.Reverse().ToList();

        Assert.Equal("Chrome פעל אצל נועה בלי התוסף של WebFilter, ולכן החיבור שלו לרשת נחסם עד שהתוסף יתחבר", events[0].Details);
        Assert.Equal("נועה", events[0].MemberName);
        // An unlinked account has no member to name.
        Assert.Equal("Edge פעל בחשבון Windows שלא שויך לבן משפחה בלי התוסף של WebFilter, ולכן החיבור שלו לרשת נחסם עד שהתוסף יתחבר", events[1].Details);
        Assert.All(events, e => Assert.True(e.IsAlert));
    }

    [Theory]
    [InlineData("extension_missing", "יואב", "דפדפן פעל אצל יואב בלי התוסף")]
    [InlineData("extension_missing:firefox", "יואב", "firefox פעל אצל יואב בלי התוסף")]
    [InlineData("extension_missing:edge", "", "Edge פעל בחשבון Windows שלא שויך")]
    public void A_missing_extension_without_a_known_browser_still_reads_well(string detail, string member, string start) =>
        Assert.StartsWith(start, EventItemViewModel.Tamper(detail, member), StringComparison.Ordinal);

    [Theory]
    [InlineData("policy_repaired")]
    [InlineData("extension_missingx:chrome")]
    [InlineData(null)]
    public void Other_tamper_kinds_are_worded_by_their_code(string? detail) => Assert.Null(EventItemViewModel.Tamper(detail, "נועה"));

    [Theory]
    [InlineData("bing:weather", "חיפוש ב-Bing: weather")]
    [InlineData("duckduckgo:a:b", "חיפוש ב-DuckDuckGo: a:b")]
    [InlineData("google:", "חיפוש ב-Google")]
    [InlineData("newengine:x", "חיפוש ב-newengine: x")]
    [InlineData(null, "חיפוש ב-")]
    public void A_search_detail_is_split_at_its_first_colon(string? detail, string text) =>
        Assert.Equal(text, Bidi.Plain(EventItemViewModel.Search(detail)));

    [Fact]
    public async Task A_member_filter_survives_a_reload()
    {
        var vm = Create();
        await vm.RefreshAsync();
        vm.SelectedMember = "yoav";

        await vm.RefreshAsync();

        Assert.Equal("yoav", vm.SelectedMember);
        Assert.Equal("yoav", LastQuery.MemberId);
    }

    [Fact]
    public async Task A_member_who_left_the_family_resets_the_filter()
    {
        var vm = Create();
        await vm.RefreshAsync();
        vm.SelectedMember = "savta";

        _client.Settings = _client.Settings with { Version = 8, Members = [.. _client.Settings.Members.Where(m => m.Id != "savta")] };
        _client.Status = TestData.Status(version: 8);
        await vm.RefreshAsync();

        Assert.Equal(ActivityViewModel.AnyValue, vm.SelectedMember);
        Assert.Null(LastQuery.MemberId);
    }
}

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

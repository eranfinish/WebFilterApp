using WebFilter.Admin.Services;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Tests;

public sealed class DashboardViewModelTests
{
    private static readonly DateTimeOffset Midnight = new(2026, 10, 20, 0, 0, 0, TimeSpan.FromHours(3));

    private readonly FakeAdminClient _client = new();

    private DashboardViewModel Create() => new(new FamilyContext(_client, new ManualTime(TestData.Now)));

    private async Task<DashboardViewModel> LoadedAsync()
    {
        var vm = Create();
        await vm.RefreshAsync();
        return vm;
    }

    [Fact]
    public async Task Shows_a_card_per_member_in_plain_words()
    {
        var vm = await LoadedAsync();

        Assert.Equal(["noa", "yoav", "dana", "savta"], vm.Members.Select(m => m.MemberId));
        var noa = vm.Members[0];
        Assert.Equal("נועה", noa.DisplayName);
        Assert.Equal("ok", noa.TimeState);
        Assert.Equal("יש זמן גלישה", noa.TimeStateText);
        Assert.Equal("היום: 45 דקות מתוך 120 דקות", noa.UsageText);
        Assert.Equal("נותרו 75 דקות", noa.RemainingText);
        Assert.Equal("שעות הגלישה נגמרות ב-19:00", noa.WindowEndText);
        Assert.True(noa.HasLimit);
        Assert.Equal(45, noa.UsedMinutes);
        Assert.Equal(120, noa.LimitMinutes);
        Assert.True(noa.IsFiltered);
        Assert.True(noa.CanExtend);
        Assert.True(noa.CanPause);
        Assert.True(noa.CanStartHomework);
        Assert.False(noa.IsPaused);
        Assert.Null(noa.PausedText);

        Assert.Equal("הזמן להיום נגמר", vm.Members[1].TimeStateText);
        Assert.Equal("נותרו 0 דקות", vm.Members[1].RemainingText);

        var dana = vm.Members[2];
        Assert.Equal("ללא הגבלת זמן", dana.TimeStateText);
        Assert.Equal("היום: 0 דקות", dana.UsageText);
        Assert.Null(dana.RemainingText);
        Assert.Null(dana.WindowEndText);
        Assert.False(dana.HasLimit);
        Assert.False(dana.IsFiltered);
        Assert.False(dana.CanExtend);
        Assert.False(dana.CanPause);
        Assert.False(dana.CanStartHomework);
        Assert.False(vm.HasNoMembers);
        Assert.Equal("עודכן ב-16:00 · מתעדכן לבד כל 10 שניות", vm.LastUpdatedText);
    }

    [Fact]
    public async Task Shows_pause_and_homework_badges_with_their_end()
    {
        _client.Status = TestData.Status(7,
            new MemberStatus("noa", "נועה", "ok", 10, 120, 110, "19:00", Midnight, TestData.Now.AddMinutes(60)),
            new MemberStatus("yoav", "יואב", "ok", 10, 240, 230, "21:30", TestData.Now.AddHours(2), null));

        var vm = await LoadedAsync();

        var noa = vm.Members[0];
        Assert.True(noa.IsPaused);
        Assert.Equal("הסינון מושהה עד סוף היום", noa.PausedText);
        Assert.False(noa.CanPause);
        Assert.True(noa.IsInHomework);
        Assert.Equal("מצב שיעורי בית עד 17:00", noa.HomeworkText);
        Assert.False(noa.CanStartHomework);
        Assert.Equal("הסינון מושהה עד 18:00", vm.Members[1].PausedText);
    }

    [Theory]
    [InlineData("add15")]
    [InlineData("add30")]
    [InlineData("pause")]
    [InlineData("resume")]
    [InlineData("homework")]
    [InlineData("stopHomework")]
    public async Task Each_quick_action_sends_its_message_and_reloads(string action)
    {
        var vm = await LoadedAsync();
        var noa = vm.Members[0];
        var (command, expected, done) = action switch
        {
            "add15" => (noa.AddQuarterHourCommand, (AdminMessage)new ExtendTime("noa", 15), "נוספו 15 דקות לנועה."),
            "add30" => (noa.AddHalfHourCommand, new ExtendTime("noa", 30), "נוספו 30 דקות לנועה."),
            "pause" => (noa.PauseCommand, new Pause("noa", Midnight), "הסינון של נועה מושהה עד סוף היום."),
            "resume" => (noa.ResumeCommand, new Resume("noa"), "הסינון של נועה חזר לפעול."),
            "homework" => (noa.StartHomeworkCommand, new StartHomework("noa", 60), "מצב שיעורי בית הופעל לנועה לשעה."),
            _ => (noa.StopHomeworkCommand, new StopHomework("noa"), "מצב שיעורי בית של נועה הסתיים."),
        };

        await command.ExecuteAsync(null);

        var commands = _client.Sent.Where(m => m is not (GetStatus or GetSettings or GetRequests or GetDevices)).ToList();
        Assert.Equal(expected, Assert.Single(commands));
        Assert.Equal(2, _client.SentOf<GetStatus>().Count());
        Assert.Equal(done, vm.StatusMessage);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Pause_lasts_until_midnight_of_the_service_clock_in_the_family_time_zone()
    {
        // 23:30 in Israel is already the next day in UTC; the pause must still end at the coming local midnight.
        _client.Status = TestData.Status() with { Now = new DateTimeOffset(2026, 10, 19, 20, 30, 0, TimeSpan.Zero) };
        var vm = await LoadedAsync();

        await vm.Members[0].PauseCommand.ExecuteAsync(null);

        Assert.Equal(Midnight, _client.Single<Pause>().Until);
    }

    [Fact]
    public async Task A_rejected_action_shows_the_reason_and_does_not_reload()
    {
        var vm = await LoadedAsync();
        _client.Reply = m => m is ExtendTime ? new AdminError("bad_request", "Unknown member 'noa'") : null;

        await vm.Members[0].AddQuarterHourCommand.ExecuteAsync(null);

        Assert.Equal("שירות הסינון לא ביצע את הפעולה: Unknown member 'noa'", vm.ErrorMessage);
        Assert.Null(vm.StatusMessage);
        Assert.Single(_client.SentOf<GetStatus>());
    }

    [Fact]
    public async Task Connection_failures_leave_the_message_to_the_window_banner()
    {
        _client.Failure = new AdminConnectionException(AdminConnectionError.ServiceNotRunning, "no pipe");
        var vm = await LoadedAsync();

        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.HasLoaded);
        Assert.Empty(vm.Members);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_timeout_is_explained_on_the_page()
    {
        _client.Failure = new AdminConnectionException(AdminConnectionError.Timeout, "slow");
        var vm = await LoadedAsync();

        Assert.Equal("שירות הסינון לא ענה בזמן. נסו שוב בעוד רגע.", vm.ErrorMessage);
    }

    [Fact]
    public async Task Counts_pending_access_requests()
    {
        var vm = await LoadedAsync();
        Assert.False(vm.HasPendingRequests);
        Assert.Equal("אין בקשות גישה שמחכות לכם", vm.PendingRequestsText);

        _client.Requests.Add(TestData.Request("ar-1", "url", "https://games.example/", "games.example"));
        await vm.RefreshAsync();
        Assert.Equal("בקשת גישה אחת מחכה להחלטה שלכם", vm.PendingRequestsText);

        _client.Requests.Add(TestData.Request("ar-2", "time", "time", "time:2026-10-19", minutes: 15));
        _client.Requests.Add(TestData.Request("ar-0", "url", "https://x.example/", "x.example", status: "denied"));
        await vm.RefreshAsync();
        Assert.True(vm.HasPendingRequests);
        Assert.Equal("2 בקשות גישה מחכות להחלטה שלכם", vm.PendingRequestsText);
        Assert.Contains(new GetRequests("pending"), _client.Sent);
    }

    [Fact]
    public async Task Refreshing_updates_cards_in_place()
    {
        var vm = await LoadedAsync();
        var noa = vm.Members[0];

        _client.Status = TestData.Status(7,
            new MemberStatus("noa", "נועה", "warn", 112, 120, 8, "19:00", null, null),
            new MemberStatus("yoav", "יואב", "ok", 0, 240, 240, null, null, null));
        await vm.RefreshAsync();

        Assert.Same(noa, vm.Members[0]);
        Assert.Equal("הזמן עומד להיגמר", noa.TimeStateText);
        Assert.Equal("נותרו 8 דקות", noa.RemainingText);
        Assert.Equal(["noa", "yoav"], vm.Members.Select(m => m.MemberId));
    }

    [Fact]
    public async Task Settings_are_fetched_again_only_when_their_version_changes()
    {
        var vm = await LoadedAsync();
        await vm.RefreshAsync();
        Assert.Single(_client.SentOf<GetSettings>());

        _client.Status = TestData.Status(version: 8);
        await vm.RefreshAsync();
        Assert.Equal(2, _client.SentOf<GetSettings>().Count());
    }

    [Fact]
    public async Task An_empty_family_says_so()
    {
        _client.Status = new StatusResult(1, "pc", true, TestData.Now, []);
        var vm = await LoadedAsync();

        Assert.True(vm.HasNoMembers);
    }
}

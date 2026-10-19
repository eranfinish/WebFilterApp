using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Tests;

public sealed class RequestsViewModelTests
{
    private readonly FakeAdminClient _client = new();

    private async Task<RequestsViewModel> LoadedAsync(params RequestDto[] requests)
    {
        _client.Requests.AddRange(requests);
        var vm = new RequestsViewModel(new FamilyContext(_client, new ManualTime(TestData.Now)));
        await vm.RefreshAsync();
        return vm;
    }

    private static Task Choose(RequestItemViewModel item, string value) =>
        ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand<string>)item.Choices.Single(c => c.Value == value).Command).ExecuteAsync(value);

    [Fact]
    public async Task Lists_pending_requests_in_plain_words()
    {
        var vm = await LoadedAsync(
            TestData.Request("ar-2", "time", "time", "time:2026-10-19", minutes: 30, member: "yoav", created: TestData.Now.AddMinutes(-1)),
            TestData.Request("ar-1", "url", "https://games.example/play", "games.example", message: " for school ", created: TestData.Now.AddMinutes(-10)));

        Assert.Equal(["ar-1", "ar-2"], vm.Pending.Select(p => p.Id));
        var url = vm.Pending[0];
        Assert.Equal("נועה", url.MemberName);
        Assert.Equal("אתר", url.KindText);
        Assert.Equal("https://games.example/play", url.TargetText);
        Assert.Equal("הודעה: \"for school\"", url.MessageText);
        Assert.Equal("נשלחה ב-19.10 15:50", url.SentText);
        Assert.True(url.IsUrl);
        Assert.Equal(["את כל האתר games.example", "רק את הדף הזה"], url.ScopeOptions.Select(o => Bidi.Plain(o.Label)));
        Assert.Equal(RequestItemViewModel.Host, url.SelectedScope);

        var time = vm.Pending[1];
        Assert.Equal("יואב", time.MemberName);
        Assert.Equal("עוד זמן", time.KindText);
        Assert.Equal("ביקש/ה עוד 30 דקות", time.TargetText);
        Assert.Null(time.MessageText);
        Assert.False(vm.HasNoPending);
        Assert.Equal("2", vm.Badge);
    }

    [Fact]
    public async Task Url_approval_choices_build_the_right_decision()
    {
        var vm = await LoadedAsync(TestData.Request("ar-1", "url", "https://games.example/play", "games.example"));
        var item = vm.Pending[0];
        Assert.Equal(["אישור ל-15 דקות", "אישור עד סוף היום", "אישור תמיד"], item.Choices.Select(c => c.Label));

        Assert.Equal(new DecideRequest("ar-1", "approve", "host", null, 15), item.BuildApproval("15"));
        Assert.Equal(new DecideRequest("ar-1", "approve", "host", "today"), item.BuildApproval("today"));
        Assert.Equal(new DecideRequest("ar-1", "approve", "host", "always"), item.BuildApproval("always"));

        item.SelectedScope = RequestItemViewModel.Page;
        Assert.Equal(new DecideRequest("ar-1", "approve", "page", "today"), item.BuildApproval("today"));
        Assert.Equal(new DecideRequest("ar-1", "approve", "page", null, 15), item.BuildApproval("15"));
    }

    [Fact]
    public async Task Approving_sends_the_decision_and_reloads()
    {
        var vm = await LoadedAsync(TestData.Request("ar-1", "url", "https://games.example/play", "games.example"));
        vm.Pending[0].SelectedScope = RequestItemViewModel.Page;
        _client.Reply = m =>
        {
            if (m is DecideRequest) _client.Requests[0] = _client.Requests[0] with { Status = "approved", DecidedAt = TestData.Now };
            return null;
        };

        await Choose(vm.Pending[0], "always");

        Assert.Equal(new DecideRequest("ar-1", "approve", "page", "always"), _client.Single<DecideRequest>());
        Assert.Equal("הבקשה של נועה אושרה.", vm.StatusMessage);
        Assert.Empty(vm.Pending);
        Assert.True(vm.HasNoPending);
        Assert.Null(vm.Badge);
        Assert.Equal("אושרה ב-19.10 16:00", Assert.Single(vm.Recent).StatusText);
    }

    [Fact]
    public async Task App_requests_approve_for_minutes_today_or_always_without_a_scope()
    {
        var vm = await LoadedAsync(TestData.Request("ar-3", "app", @"C:\Games\game.exe", @"c:\games\game.exe"));
        var item = vm.Pending[0];

        Assert.False(item.IsUrl);
        Assert.Equal(new DecideRequest("ar-3", "approve", null, null, 15), item.BuildApproval("15"));
        Assert.Equal(new DecideRequest("ar-3", "approve", null, "today"), item.BuildApproval("today"));
        Assert.Equal(new DecideRequest("ar-3", "approve", null, "always"), item.BuildApproval("always"));
    }

    [Fact]
    public async Task Channel_requests_offer_a_single_permanent_approval()
    {
        var vm = await LoadedAsync(TestData.Request("ar-4", "youtube", "UCgamerX0000000000000003", "UCgamerX0000000000000003"));
        var item = vm.Pending[0];

        var choice = Assert.Single(item.Choices);
        Assert.Equal("אישור הערוץ", choice.Label);
        await Choose(item, choice.Value);
        Assert.Equal(new DecideRequest("ar-4", "approve", Duration: "always"), _client.Single<DecideRequest>());
    }

    [Fact]
    public async Task Time_requests_grant_minutes_and_may_grant_less_than_asked()
    {
        var vm = await LoadedAsync(TestData.Request("ar-5", "time", "time", "time:2026-10-19", minutes: 30));
        var item = vm.Pending[0];
        Assert.Equal(["לתת עוד 15 דקות", "לתת עוד 30 דקות"], item.Choices.Select(c => c.Label));

        await Choose(item, "15");

        Assert.Equal(new DecideRequest("ar-5", "approve", Minutes: 15), _client.Single<DecideRequest>());
    }

    [Fact]
    public async Task An_unusual_time_request_is_offered_as_asked()
    {
        var vm = await LoadedAsync(TestData.Request("ar-6", "time", "time", "time:2026-10-19", minutes: 45));

        Assert.Equal(["15", "30", "45"], vm.Pending[0].Choices.Select(c => c.Value));
    }

    [Fact]
    public async Task Denying_sends_a_deny_decision()
    {
        var vm = await LoadedAsync(TestData.Request("ar-1", "url", "https://games.example/", "games.example"));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.Pending[0].DenyCommand).ExecuteAsync(null);

        Assert.Equal(new DecideRequest("ar-1", "deny"), _client.Single<DecideRequest>());
        Assert.Equal("הבקשה של נועה נדחתה.", vm.StatusMessage);
    }

    [Fact]
    public void Unknown_approval_choices_are_rejected()
    {
        var item = new RequestItemViewModel(TestData.Request("ar-1", "url", "https://a.example/", "a.example"), new FamilyContext(_client), owner: null);

        Assert.Throws<ArgumentException>(() => item.BuildApproval("forever"));
        Assert.Empty(item.Choices);
    }

    [Fact]
    public async Task Recent_decisions_are_newest_first()
    {
        var vm = await LoadedAsync(
            TestData.Request("ar-1", "url", "https://a.example/", "a.example", status: "denied", decided: TestData.Now.AddHours(-2)),
            TestData.Request("ar-2", "url", "https://b.example/", "b.example", status: "approved", decided: TestData.Now.AddMinutes(-3)),
            TestData.Request("ar-3", "url", "https://c.example/", "c.example"));

        Assert.Equal(["ar-2", "ar-1"], vm.Recent.Select(r => r.Id));
        Assert.Equal("נדחתה ב-19.10 14:00", vm.Recent[1].StatusText);
        Assert.False(vm.Recent[1].IsApproved);
        Assert.Empty(vm.Recent[0].Choices);
        Assert.Equal(["ar-3"], vm.Pending.Select(p => p.Id));
        Assert.Contains(new GetRequests("pending"), _client.Sent);
        Assert.Contains(new GetRequests(), _client.Sent);
    }

    [Fact]
    public async Task A_refresh_keeps_the_scope_the_parent_picked()
    {
        var vm = await LoadedAsync(TestData.Request("ar-1", "url", "https://games.example/play", "games.example"));
        var item = vm.Pending[0];
        item.SelectedScope = RequestItemViewModel.Page;

        _client.Requests.Add(TestData.Request("ar-2", "url", "https://b.example/", "b.example"));
        await vm.RefreshAsync();

        Assert.Same(item, vm.Pending[0]);
        Assert.Equal(RequestItemViewModel.Page, item.SelectedScope);
        Assert.Equal(2, vm.Pending.Count);
    }

    [Fact]
    public async Task A_decision_the_service_rejects_is_explained()
    {
        var vm = await LoadedAsync(TestData.Request("ar-1", "url", "https://games.example/", "games.example"));
        _client.Reply = m => m is DecideRequest ? new AdminError("bad_request", "Unknown request 'ar-1'") : null;

        await Choose(vm.Pending[0], "today");

        Assert.Equal("שירות הסינון לא ביצע את הפעולה: Unknown request 'ar-1'", vm.ErrorMessage);
        Assert.Null(vm.StatusMessage);
    }
}

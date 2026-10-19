using WebFilter.Admin.Services;
using WebFilter.Admin.ViewModels;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Tests;

public sealed class MainViewModelTests
{
    private readonly FakeAdminClient _client = new();
    private readonly ManualTimer _timer = new();

    private MainViewModel Create() => new(_client, _timer, new FakeDialogs(), new ManualTime(TestData.Now));

    [Fact]
    public async Task Starts_on_the_dashboard_and_refreshes_every_10_seconds()
    {
        using var main = Create();

        main.Start();
        await main.CurrentLoad;

        Assert.Same(main.Dashboard, main.SelectedPage);
        Assert.Equal(TimeSpan.FromSeconds(10), _timer.Interval);
        Assert.Single(_client.SentOf<GetStatus>());
        Assert.Equal(4, main.Dashboard.Members.Count);

        await _timer.FireAsync();
        Assert.Equal(2, _client.SentOf<GetStatus>().Count());
    }

    [Fact]
    public void Lists_the_five_screens_in_order()
    {
        using var main = Create();

        Assert.Equal(["לוח בקרה", "בקשות גישה", "כללי אתרים", "יומן פעילות", "הגדרות"], main.Pages.Select(p => p.Title));
        Assert.All(main.Pages, p => Assert.Same(main, p.Navigator));
    }

    [Fact]
    public async Task Pages_without_auto_refresh_only_update_the_request_count_on_a_tick()
    {
        using var main = Create();
        main.Start();
        main.SelectedPage = main.Rules;
        await main.CurrentLoad;
        var statusCalls = _client.SentOf<GetStatus>().Count();
        _client.Requests.Add(TestData.Request("ar-1", "url", "https://games.example/", "games.example"));

        await _timer.FireAsync();

        Assert.Equal(statusCalls, _client.SentOf<GetStatus>().Count());
        Assert.Equal(1, main.Context.PendingRequestCount);
        Assert.Equal("1", main.Requests.Badge);
    }

    [Fact]
    public async Task The_dashboard_opens_the_requests_page()
    {
        using var main = Create();
        main.Start();
        await main.CurrentLoad;

        main.Dashboard.OpenRequestsCommand.Execute(null);
        await main.CurrentLoad;

        Assert.Same(main.Requests, main.SelectedPage);
        Assert.Contains(new GetRequests(), _client.Sent);
    }

    [Theory]
    [InlineData(AdminConnectionState.ServiceNotRunning, true, "שירות הסינון לא פועל במחשב הזה", "שירות הסינון לא פועל")]
    [InlineData(AdminConnectionState.NotElevated, true, "אין לממשק הרשאת ניהול", "אין הרשאת ניהול")]
    [InlineData(AdminConnectionState.ConnectionLost, true, "החיבור לשירות הסינון נותק", "החיבור נותק")]
    [InlineData(AdminConnectionState.Connected, false, "מחובר לשירות הסינון", "מחובר לשירות הסינון")]
    [InlineData(AdminConnectionState.NotConnected, false, "עדיין לא מחובר", "לא מחובר")]
    public void The_banner_follows_the_connection_state(AdminConnectionState state, bool problem, string text, string shortText)
    {
        using var main = Create();

        _client.State = state;

        Assert.Equal(problem, main.HasConnectionProblem);
        Assert.StartsWith(text, main.ConnectionText);
        Assert.Equal(shortText, main.ConnectionShortText);
        Assert.Equal(state == AdminConnectionState.Connected, main.IsConnected);
    }

    [Fact]
    public void A_reconnect_attempt_keeps_the_problem_banner()
    {
        using var main = Create();
        _client.State = AdminConnectionState.ServiceNotRunning;

        _client.State = AdminConnectionState.Connecting;

        Assert.True(main.HasConnectionProblem);
        Assert.StartsWith("שירות הסינון לא פועל", main.ConnectionText);
    }

    [Fact]
    public async Task Retry_reloads_the_current_page_after_the_service_comes_back()
    {
        using var main = Create();
        _client.Failure = new AdminConnectionException(AdminConnectionError.ServiceNotRunning, "no pipe");
        main.Start();
        await main.CurrentLoad;
        Assert.Empty(main.Dashboard.Members);

        _client.Failure = null;
        await main.RetryCommand.ExecuteAsync(null);

        Assert.Equal(4, main.Dashboard.Members.Count);
    }

    [Fact]
    public void Dispose_stops_the_timer_and_the_state_updates()
    {
        var main = Create();
        main.Start();

        main.Dispose();
        _client.State = AdminConnectionState.ServiceNotRunning;

        Assert.True(_timer.Stopped);
        Assert.False(main.HasConnectionProblem);
    }
}

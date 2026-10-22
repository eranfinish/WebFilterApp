using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier.Tests;

/// <summary>How often the notifier polls, how it backs off while the service is away, and when it gives up.</summary>
public sealed class NotifierLoopTests
{
    private readonly MemoryCursor _cursor = new() { Stored = TestData.Now };
    private readonly ListLog _log = new();
    private readonly List<TimeSpan> _waits = [];
    private readonly List<IReadOnlyList<NotificationDto>> _shown = [];
    private readonly CancellationTokenSource _stop = new();

    /// <summary>
    /// A loop whose waits return at once and are recorded; before each poll <paramref name="answers"/> picks what the
    /// pipe does (by poll number, from 0); it is stopped after <paramref name="polls"/> polls.
    /// </summary>
    private Task<NotifierExit> RunAsync(int polls, Func<int, NotifyMessage?> answers)
    {
        var poll = -1;
        var client = new FakePipeClient(request =>
        {
            var since = ((GetNotifications)request).Since;
            return answers(poll) is { } reply
                ? reply is NotificationsResult result ? new NotificationsResult([.. result.Items.Where(i => i.At > since)]) : reply
                : throw new NotifyUnavailableException("no pipe");
        });
        var loop = new NotifierLoop(new NotificationPoller(client, _cursor, new ManualTime(TestData.Now), _log), (wait, ct) =>
        {
            _waits.Add(wait);
            if (++poll == polls) _stop.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, _log);
        return loop.RunAsync(_shown.Add, _stop.Token);
    }

    [Fact]
    public async Task Polls_at_once_then_every_30_seconds_and_hands_over_what_arrives()
    {
        var exit = await RunAsync(3, poll => poll switch
        {
            1 => new NotificationsResult([TestData.Request(1), TestData.Tamper(2, "network_repaired")]),
            _ => new NotificationsResult([]),
        });

        Assert.Equal(NotifierExit.Stopped, exit);
        Assert.Equal([TimeSpan.Zero, NotifierLoop.Interval, NotifierLoop.Interval, NotifierLoop.Interval], _waits);
        Assert.Equal(["request:1", "tamper:2"], Assert.Single(_shown).Select(i => i.Id));
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task A_user_who_is_not_an_administrator_ends_the_notifier()
    {
        var exit = await RunAsync(5, _ => new NotifyError(NotifyError.NotAdmin, "Notifications are for the parents' (administrator) accounts."));

        Assert.Equal(NotifierExit.NotAdmin, exit);
        Assert.Equal([TimeSpan.Zero], _waits);
        Assert.Empty(_shown);
        Assert.Contains("not an administrator", Assert.Single(_log.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_the_pipe_is_missing_it_waits_longer_each_time_and_logs_once()
    {
        var exit = await RunAsync(8, poll => poll is >= 0 and < 6 ? null : new NotificationsResult([TestData.Request(5)]));

        Assert.Equal(NotifierExit.Stopped, exit);
        Assert.Equal([0, 60, 120, 240, 300, 300, 300, 30, 30], _waits.Select(w => (int)w.TotalSeconds));
        Assert.Equal(["The notify pipe is not available; polling less often until it is back.", "The notify pipe is back."], _log.Lines);
        Assert.Single(_shown);
    }

    [Fact]
    public async Task Service_errors_do_not_slow_it_down_or_stop_it()
    {
        var exit = await RunAsync(3, _ => new NotifyError("internal_error", "database locked"));

        Assert.Equal(NotifierExit.Stopped, exit);
        Assert.Equal([0, 30, 30, 30], _waits.Select(w => (int)w.TotalSeconds));
        Assert.Equal(3, _log.Lines.Count);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 60)]
    [InlineData(2, 120)]
    [InlineData(3, 240)]
    [InlineData(4, 300)]
    [InlineData(100, 300)]
    public void The_wait_doubles_up_to_five_minutes(int misses, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), NotifierLoop.WaitAfter(misses));
}

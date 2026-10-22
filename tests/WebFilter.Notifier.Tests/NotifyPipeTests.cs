using System.Text;
using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier.Tests;

/// <summary>
/// The notifier against a real named pipe with a unique name, played by <see cref="FakeNotifyServer"/> the way the hub
/// service's NotifyPipeServer answers (WebFilter.Service.Tests checks the service itself).
/// </summary>
public sealed class NotifyPipeTests
{
    private readonly MemoryCursor _cursor = new() { Stored = TestData.Now };
    private readonly ListLog _log = new();

    private NotificationPoller Poller(string pipe, TimeSpan? replyTimeout = null) =>
        new(new NotifyPipeClient(pipe, TimeSpan.FromSeconds(2), replyTimeout ?? TimeSpan.FromSeconds(5)), _cursor, new ManualTime(TestData.Now), _log);

    [Fact]
    public async Task Notifications_come_over_the_pipe_with_the_cursor_and_the_request_id()
    {
        var stored = new List<NotificationDto> { TestData.Request(-1), TestData.Request(1), TestData.Tamper(2, "policy_repaired") };
        await using var server = new FakeNotifyServer(request => request is GetNotifications get
            ? new NotificationsResult([.. stored.Where(i => i.At > get.Since)])
            : new NotifyError("unsupported", "Only getNotifications is served here."));
        var poller = Poller(server.Name);

        var first = await poller.PollAsync(CancellationToken.None);
        var second = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(PollStatus.Ok, first.Status);
        Assert.Equal(["request:1", "tamper:2"], first.Items.Select(i => i.Id));
        Assert.Equal(("noa", "pc-kids", "url:games.example"), (first.Items[0].MemberId, first.Items[0].DeviceId, first.Items[0].Detail));
        Assert.Empty(second.Items);
        Assert.Equal([TestData.Now, TestData.Now.AddMinutes(2)], server.Received.Select(r => ((GetNotifications)r).Since));
        Assert.All(server.Received, r => Assert.False(string.IsNullOrEmpty(r.Id)));
        Assert.Equal(TestData.Now.AddMinutes(2), _cursor.Stored);
    }

    [Fact]
    public async Task A_refused_user_hears_not_admin_and_the_pipe_hangs_up()
    {
        await using var server = new FakeNotifyServer(_ => new NotifyError(NotifyError.NotAdmin, "Notifications are for the parents' (administrator) accounts."));

        var result = await Poller(server.Name).PollAsync(CancellationToken.None);

        Assert.Equal(PollStatus.NotAdmin, result.Status);
    }

    [Fact]
    public async Task No_service_means_unavailable_not_an_error()
    {
        var poller = new NotificationPoller(new NotifyPipeClient($"WebFilter.Test.Notify.{Guid.NewGuid():N}", TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(1)),
            _cursor, new ManualTime(TestData.Now), _log);

        var result = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(PollStatus.Unavailable, result.Status);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task A_service_that_hangs_up_or_never_answers_is_unavailable()
    {
        await using var hangsUp = new FakeNotifyServer(_ => null);
        Assert.Equal(PollStatus.Unavailable, (await Poller(hangsUp.Name).PollAsync(CancellationToken.None)).Status);

        var never = new TaskCompletionSource();
        await using var silent = new FakeNotifyServer(_ => never.Task);
        Assert.Equal(PollStatus.Unavailable, (await Poller(silent.Name, TimeSpan.FromMilliseconds(300)).PollAsync(CancellationToken.None)).Status);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task A_reply_that_is_not_json_is_logged_and_the_notifier_goes_on()
    {
        await using var server = new FakeNotifyServer(_ => Encoding.UTF8.GetBytes("<html>not a message</html>"));

        var result = await Poller(server.Name).PollAsync(CancellationToken.None);

        Assert.Equal(PollStatus.Failed, result.Status);
        Assert.StartsWith("The notify pipe sent a reply this app cannot read", Assert.Single(_log.Lines), StringComparison.Ordinal);
        Assert.Equal(TestData.Now, _cursor.Stored);
    }

    [Fact]
    public async Task A_message_type_from_a_newer_service_is_logged_and_ignored()
    {
        await using var server = new FakeNotifyServer(request => Encoding.UTF8.GetBytes($$"""{"type":"notificationsDigest","v":2,"id":"{{request.Id}}"}"""));

        var result = await Poller(server.Name).PollAsync(CancellationToken.None);

        Assert.Equal(PollStatus.Failed, result.Status);
        Assert.Single(_log.Lines);
    }

    [Fact]
    public async Task The_whole_loop_runs_over_the_pipe_until_the_service_refuses()
    {
        var calls = 0;
        await using var server = new FakeNotifyServer(_ => ++calls switch
        {
            1 => new NotificationsResult([TestData.Request(1)]),
            2 => new NotificationsResult([]),
            _ => new NotifyError(NotifyError.NotAdmin, "Notifications are for the parents' (administrator) accounts."),
        });
        var shown = new List<IReadOnlyList<NotificationDto>>();
        var loop = new NotifierLoop(Poller(server.Name), (_, _) => Task.CompletedTask, _log);

        var exit = await loop.RunAsync(shown.Add, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(NotifierExit.NotAdmin, exit);
        Assert.Equal("request:1", Assert.Single(Assert.Single(shown)).Id);
        Assert.Equal(3, server.Received.Count);
    }
}

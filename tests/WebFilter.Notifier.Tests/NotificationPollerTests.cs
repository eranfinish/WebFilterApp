using System.IO;
using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier.Tests;

/// <summary>PROTECTION-SPEC §10: what the notifier asks the hub for, and where it keeps its place.</summary>
public sealed class NotificationPollerTests
{
    private readonly MemoryCursor _cursor = new();
    private readonly ListLog _log = new();
    private readonly ManualTime _clock = new(TestData.Now);

    private NotificationPoller Poller(INotifyPipeClient client) => new(client, _cursor, _clock, _log);

    [Fact]
    public async Task The_first_run_starts_from_now_so_the_kept_days_are_not_replayed()
    {
        var old = new List<NotificationDto> { TestData.Request(-60 * 24 * 3), TestData.Tamper(-5, "policy_repaired") };
        var client = FakePipeClient.Serving(old);
        var poller = Poller(client);

        var result = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(PollStatus.Ok, result.Status);
        Assert.Empty(result.Items);
        Assert.Equal(TestData.Now, Assert.Single(client.Asked).Since);
        Assert.Equal(TestData.Now, _cursor.Stored);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task New_notifications_move_the_cursor_to_the_last_one_and_are_not_shown_again()
    {
        _cursor.Stored = TestData.Now;
        var items = new List<NotificationDto> { TestData.Request(-1), TestData.Request(2), TestData.Tamper(3, "unclean_stop") };
        var client = FakePipeClient.Serving(items);
        var poller = Poller(client);

        var first = await poller.PollAsync(CancellationToken.None);
        var second = await poller.PollAsync(CancellationToken.None);

        Assert.Equal(["request:2", "tamper:3"], first.Items.Select(i => i.Id));
        Assert.Equal(TestData.Now.AddMinutes(3), _cursor.Stored);
        Assert.Equal(PollStatus.Ok, second.Status);
        Assert.Empty(second.Items);
        Assert.Equal([TestData.Now, TestData.Now.AddMinutes(3)], client.Asked.Select(a => a.Since));
    }

    [Fact]
    public async Task A_cursor_stored_by_an_earlier_run_is_where_the_next_run_goes_on()
    {
        _cursor.Stored = TestData.Now.AddHours(-10);
        var client = FakePipeClient.Serving([TestData.Request(-120), TestData.Device(-30, null)]);

        var result = await Poller(client).PollAsync(CancellationToken.None);

        Assert.Equal(["request:-120", "device:-30"], result.Items.Select(i => i.Id));
        Assert.Equal(TestData.Now.AddHours(-10), client.Asked[0].Since);
    }

    [Fact]
    public async Task Full_pages_are_read_on_in_the_same_poll()
    {
        _cursor.Stored = TestData.Now.AddDays(-1);
        var items = Enumerable.Range(1, 230).Select(i => TestData.Request(-i)).ToList();
        var client = FakePipeClient.Serving(items);

        var result = await Poller(client).PollAsync(CancellationToken.None);

        Assert.Equal(230, result.Items.Count);
        Assert.Equal(3, client.Asked.Count);
        Assert.Equal(TestData.Now.AddMinutes(-1), _cursor.Stored);
        Assert.Equal(result.Items.OrderBy(i => i.At), result.Items);
    }

    [Fact]
    public async Task Items_at_or_before_the_cursor_are_never_shown_even_if_sent()
    {
        _cursor.Stored = TestData.Now;
        var client = new FakePipeClient(_ => new NotificationsResult([TestData.Request(0), TestData.Request(-3), TestData.Request(4)]));

        var result = await Poller(client).PollAsync(CancellationToken.None);

        Assert.Equal(["request:4"], result.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task A_user_who_is_not_an_administrator_is_told_so()
    {
        var client = new FakePipeClient(_ => new NotifyError(NotifyError.NotAdmin, "Notifications are for the parents' (administrator) accounts."));

        var result = await Poller(client).PollAsync(CancellationToken.None);

        Assert.Equal(PollStatus.NotAdmin, result.Status);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task A_missing_pipe_is_quiet_and_keeps_the_cursor()
    {
        _cursor.Stored = TestData.Now;
        var client = FakePipeClient.Throwing(new NotifyUnavailableException("no pipe"));

        var result = await Poller(client).PollAsync(CancellationToken.None);

        Assert.Equal(PollResult.Of(PollStatus.Unavailable).Status, result.Status);
        Assert.Equal(TestData.Now, _cursor.Stored);
        Assert.Equal(0, _cursor.Saves);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task A_refusal_or_an_unreadable_reply_is_logged_and_ignored()
    {
        _cursor.Stored = TestData.Now;

        var refused = await Poller(new FakePipeClient(_ => new NotifyError("internal_error", "database locked"))).PollAsync(CancellationToken.None);
        var unreadable = await Poller(FakePipeClient.Throwing(new InvalidDataException("The notify pipe sent a reply this app cannot read: '<' is invalid")))
            .PollAsync(CancellationToken.None);
        var unexpected = await Poller(new FakePipeClient(r => r)).PollAsync(CancellationToken.None);

        Assert.All([refused, unreadable, unexpected], r => Assert.Equal(PollStatus.Failed, r.Status));
        Assert.Equal(
            ["The notify pipe answered internal_error: database locked", "The notify pipe sent a reply this app cannot read: '<' is invalid",
             "The notify pipe answered with an unexpected GetNotifications."],
            _log.Lines);
        Assert.Equal(TestData.Now, _cursor.Stored);
    }

    [Fact]
    public async Task An_unreadable_cursor_is_logged_and_the_notifier_starts_from_now()
    {
        _cursor.LoadFailure = new InvalidDataException("notifier-cursor.txt does not hold a time: 'x'.");
        var client = FakePipeClient.Serving([TestData.Request(-1)]);

        var result = await Poller(client).PollAsync(CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(TestData.Now, client.Asked[0].Since);
        Assert.StartsWith("The cursor could not be read", Assert.Single(_log.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cursor_that_cannot_be_saved_is_kept_in_memory()
    {
        _cursor.Stored = TestData.Now;
        _cursor.SaveFailure = new IOException("disk full");
        var items = new List<NotificationDto> { TestData.Request(1) };
        var client = FakePipeClient.Serving(items);
        var poller = Poller(client);

        Assert.Single((await poller.PollAsync(CancellationToken.None)).Items);
        Assert.Empty((await poller.PollAsync(CancellationToken.None)).Items);

        Assert.Equal(TestData.Now.AddMinutes(1), poller.Since);
        Assert.Equal("The cursor could not be saved: disk full", Assert.Single(_log.Lines));
    }

    [Fact]
    public async Task A_reply_without_its_list_reads_as_empty()
    {
        _cursor.Stored = TestData.Now;
        var client = new FakePipeClient(_ => new NotificationsResult(null!));

        var result = await Poller(client).PollAsync(CancellationToken.None);

        Assert.Equal(PollStatus.Ok, result.Status);
        Assert.Empty(result.Items);
    }
}

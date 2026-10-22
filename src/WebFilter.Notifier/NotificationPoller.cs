using System.IO;
using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier;

public enum PollStatus
{
    /// <summary>The service answered; <see cref="PollResult.Items"/> are the new notifications (maybe none).</summary>
    Ok,

    /// <summary>No pipe or no answer: the service is stopped or starting. Try again later, quietly.</summary>
    Unavailable,

    /// <summary>The service does not answer this user (not an administrator): the notifier has nothing to do.</summary>
    NotAdmin,

    /// <summary>A refusal or a reply this app cannot read; logged. Items read before it are still returned.</summary>
    Failed,
}

public sealed record PollResult(PollStatus Status, IReadOnlyList<NotificationDto> Items)
{
    public static PollResult Of(PollStatus status) => new(status, []);
}

/// <summary>
/// Asks the hub for the notifications after the cursor (<see cref="GetNotifications"/>), page by page, and moves the cursor
/// to the last one received. The cursor is kept per user (<see cref="ICursorStore"/>); without one — the first run — it
/// starts from now, so a new user is not shown the 30 days the hub keeps. Never throws for what the service answers.
/// </summary>
public sealed class NotificationPoller(INotifyPipeClient client, ICursorStore cursor, TimeProvider clock, INotifierLog log)
{
    /// <summary>Pages read in one poll at most (<see cref="NotificationsResult.MaxItems"/> each); the rest comes next poll.</summary>
    public const int MaxPages = 10;

    private DateTimeOffset? _since;

    /// <summary>The cursor now: the <c>at</c> of the last notification received, or where the notifier started.</summary>
    public DateTimeOffset? Since => _since;

    public async Task<PollResult> PollAsync(CancellationToken ct)
    {
        var since = _since ??= StartingPoint();
        var received = new List<NotificationDto>();
        for (var page = 0; page < MaxPages; page++)
        {
            NotifyMessage reply;
            try
            {
                reply = await client.RequestAsync(new GetNotifications(since), ct);
            }
            catch (NotifyUnavailableException)
            {
                return received.Count == 0 ? PollResult.Of(PollStatus.Unavailable) : new PollResult(PollStatus.Ok, received);
            }
            catch (InvalidDataException ex)
            {
                log.Write(ex.Message);
                return new PollResult(PollStatus.Failed, received);
            }

            switch (reply)
            {
                case NotificationsResult result:
                    // Oldest first and after the cursor, as the service promises; anything else is not shown twice. A reply
                    // without its list (a JSON null) reads as empty.
                    var all = result.Items ?? [];
                    var items = all.Where(i => i is not null && i.At > since).OrderBy(i => i.At).ToList();
                    if (items.Count > 0)
                    {
                        received.AddRange(items);
                        since = items[^1].At;
                        Move(since);
                    }
                    if (all.Count < NotificationsResult.MaxItems || items.Count == 0) return new PollResult(PollStatus.Ok, received);
                    break;
                case NotifyError { Code: NotifyError.NotAdmin }:
                    return new PollResult(PollStatus.NotAdmin, received);
                case NotifyError error:
                    log.Write($"The notify pipe answered {error.Code}: {error.Message}");
                    return new PollResult(PollStatus.Failed, received);
                default:
                    log.Write($"The notify pipe answered with an unexpected {reply.GetType().Name}.");
                    return new PollResult(PollStatus.Failed, received);
            }
        }
        return new PollResult(PollStatus.Ok, received);
    }

    /// <summary>The stored cursor; on the first run (or an unreadable one) now, stored at once.</summary>
    private DateTimeOffset StartingPoint()
    {
        try
        {
            if (cursor.Load() is { } stored) return stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log.Write($"The cursor could not be read ({ex.Message}); starting from now.");
        }
        var now = clock.GetUtcNow();
        Store(now);
        return now;
    }

    private void Move(DateTimeOffset since)
    {
        _since = since;
        Store(since);
    }

    /// <summary>A cursor that cannot be stored stays in memory: at worst the next start shows a few again.</summary>
    private void Store(DateTimeOffset at)
    {
        try
        {
            cursor.Save(at);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"The cursor could not be saved: {ex.Message}");
        }
    }
}

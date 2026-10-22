using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier;

/// <summary>Why <see cref="NotifierLoop.RunAsync"/> returned.</summary>
public enum NotifierExit
{
    /// <summary>Cancelled: the user chose "exit", or Windows is ending the session.</summary>
    Stopped,

    /// <summary>The service does not answer this user: not an administrator, so nothing will ever come.</summary>
    NotAdmin,
}

/// <summary>
/// Polls about every <see cref="Interval"/> and hands what arrives to the tray. While the pipe is missing (the service is
/// stopped or still starting) it waits longer each time, up to <see cref="MaxWait"/>, and says so only in the log, once.
/// </summary>
public sealed class NotifierLoop(NotificationPoller poller, Func<TimeSpan, CancellationToken, Task> delay, INotifierLog log)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);

    /// <summary>The wait after <paramref name="misses"/> polls in a row found no pipe: 30 s, 1, 2, 4, then 5 minutes.</summary>
    public static TimeSpan WaitAfter(int misses) =>
        misses <= 0 ? Interval : TimeSpan.FromTicks(Math.Min(MaxWait.Ticks, Interval.Ticks << Math.Min(misses, 16)));

    /// <summary>Polls at once, then on and on until cancelled or refused; <paramref name="show"/> gets each poll's new items.</summary>
    public async Task<NotifierExit> RunAsync(Action<IReadOnlyList<NotificationDto>> show, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(show);
        var wait = TimeSpan.Zero;
        var misses = 0;
        try
        {
            while (true)
            {
                await delay(wait, ct);
                var result = await poller.PollAsync(ct);
                if (result.Items.Count > 0) show(result.Items);
                switch (result.Status)
                {
                    case PollStatus.NotAdmin:
                        log.Write("The service does not answer this user (not an administrator); the notifier exits.");
                        return NotifierExit.NotAdmin;
                    case PollStatus.Unavailable:
                        if (misses++ == 0) log.Write("The notify pipe is not available; polling less often until it is back.");
                        break;
                    default:
                        if (misses > 0) log.Write("The notify pipe is back.");
                        misses = 0;
                        break;
                }
                wait = WaitAfter(misses);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return NotifierExit.Stopped;
        }
    }
}

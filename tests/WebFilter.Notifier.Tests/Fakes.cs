using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using WebFilter.Contracts.Framing;
using WebFilter.Contracts.Notify;
using WebFilter.Notifier.Resources;

namespace WebFilter.Notifier.Tests;

internal static class TestSetup
{
    /// <summary>The notifier pins Hebrew at start; tests do the same so wording does not depend on the machine's language.</summary>
    [ModuleInitializer]
    internal static void PinHebrew()
    {
        var hebrew = CultureInfo.GetCultureInfo("he-IL");
        CultureInfo.DefaultThreadCurrentCulture = hebrew;
        CultureInfo.DefaultThreadCurrentUICulture = hebrew;
        Strings.Culture = hebrew;
    }
}

internal static class TestData
{
    /// <summary>Thursday 22 Oct 2026, 20:00 in Israel (UTC+3).</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 22, 20, 0, 0, TimeSpan.FromHours(3));

    public static NotificationDto Request(int minutes, string detail = "url:games.example", string? device = "pc-kids") =>
        new($"request:{minutes}", NotificationKinds.Request, Now.AddMinutes(minutes), "noa", device, detail);

    public static NotificationDto Tamper(int minutes, string detail, string? device = "pc-kids") =>
        new($"tamper:{minutes}", NotificationKinds.Tamper, Now.AddMinutes(minutes), null, device, detail);

    public static NotificationDto Device(int minutes, string? lastSeen, string device = "laptop-yoav") =>
        new($"device:{minutes}", NotificationKinds.Device, Now.AddMinutes(minutes), null, device, lastSeen);
}

internal static class Bidi
{
    /// <summary>The text without the invisible direction marks around left-to-right values.</summary>
    public static string Plain(string text) => text.Replace("‎", "", StringComparison.Ordinal);
}

/// <summary>A pipe client that answers from a function and records what it was asked.</summary>
internal sealed class FakePipeClient(Func<NotifyMessage, NotifyMessage> answer) : INotifyPipeClient
{
    public List<GetNotifications> Asked { get; } = [];

    public Task<NotifyMessage> RequestAsync(NotifyMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Asked.Add((GetNotifications)request);
        return Task.FromResult(answer(request));
    }

    /// <summary>Like the service: the stored items after <c>since</c>, oldest first, at most a page.</summary>
    public static FakePipeClient Serving(List<NotificationDto> items) => new(request =>
    {
        var since = ((GetNotifications)request).Since;
        return new NotificationsResult([.. items.Where(i => since is null || i.At > since).OrderBy(i => i.At).Take(NotificationsResult.MaxItems)]);
    });

    public static FakePipeClient Throwing(Exception error) => new(_ => throw error);
}

internal sealed class MemoryCursor : ICursorStore
{
    public DateTimeOffset? Stored { get; set; }

    public int Saves { get; private set; }

    public Exception? LoadFailure { get; set; }

    public Exception? SaveFailure { get; set; }

    public DateTimeOffset? Load() => LoadFailure is { } failure ? throw failure : Stored;

    public void Save(DateTimeOffset at)
    {
        if (SaveFailure is { } failure) throw failure;
        Stored = at;
        Saves++;
    }
}

internal sealed class ListLog : INotifierLog
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public void Write(string message) => Lines.Enqueue(message);
}

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// Plays the hub service on a pipe of its own name: answers each request with what <c>answer</c> returns — a message
/// (its id set to the request's), raw bytes (a frame of them), or null to hang up — like the service's NotifyPipeServer.
/// </summary>
internal sealed class FakeNotifyServer : IAsyncDisposable
{
    private readonly Func<NotifyMessage, object?> _answer;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeNotifyServer(Func<NotifyMessage, object?> answer)
    {
        _answer = answer;
        var ready = new TaskCompletionSource();
        _loop = Task.Run(() => ServeAsync(ready));
        ready.Task.Wait(TimeSpan.FromSeconds(5));
    }

    public string Name { get; } = $"WebFilter.Test.Notify.{Guid.NewGuid():N}";

    public ConcurrentQueue<NotifyMessage> Received { get; } = new();

    /// <summary>Released when the test ends, for answers that never come.</summary>
    public CancellationToken Stopping => _stop.Token;

    private async Task ServeAsync(TaskCompletionSource ready)
    {
        while (!_stop.IsCancellationRequested)
        {
            // The next instance exists before a client is served, so the name is never missing between two requests.
            var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            ready.TrySetResult();
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            _ = Task.Run(() => ServeOneAsync(pipe));
        }
    }

    private async Task ServeOneAsync(NamedPipeServerStream pipe)
    {
        await using var owned = pipe;
        try
        {
            while (await NotifyProtocol.ReadAsync(pipe, _stop.Token) is { } request)
            {
                Received.Enqueue(request);
                switch (_answer(request))
                {
                    case NotifyMessage reply:
                        await NotifyProtocol.WriteAsync(pipe, reply with { Id = request.Id }, _stop.Token);
                        break;
                    case byte[] raw:
                        await MessageFraming.WriteAsync(pipe, raw, _stop.Token);
                        break;
                    case Task wait:
                        await wait.WaitAsync(_stop.Token);
                        break;
                    default:
                        return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { }
        _stop.Dispose();
    }
}

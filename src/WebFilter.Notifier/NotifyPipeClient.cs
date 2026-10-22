using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier;

/// <summary>One request and its reply on the notify pipe (PROTECTION-SPEC §10). Replaced by a fake in tests.</summary>
public interface INotifyPipeClient
{
    /// <summary>Sends <paramref name="request"/> and returns the service's reply.</summary>
    /// <exception cref="NotifyUnavailableException">No pipe (the service is not running, or this is not the hub) or no answer.</exception>
    /// <exception cref="InvalidDataException">The reply is not a message this app can read.</exception>
    Task<NotifyMessage> RequestAsync(NotifyMessage request, CancellationToken ct);
}

/// <summary>The notify pipe is not there or did not answer: the service is stopped, starting, or not a hub's.</summary>
public sealed class NotifyUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// <c>\\.\pipe\WebFilter.Notify</c> (or a test pipe): a connection per request, so a restarted service is simply found on
/// the next poll.
/// </summary>
public sealed class NotifyPipeClient(string pipeName, TimeSpan connectTimeout, TimeSpan replyTimeout) : INotifyPipeClient
{
    public static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultReplyTimeout = TimeSpan.FromSeconds(10);

    public NotifyPipeClient(string pipeName) : this(pipeName, DefaultConnectTimeout, DefaultReplyTimeout)
    {
    }

    public string PipeName { get; } = pipeName;

    public async Task<NotifyMessage> RequestAsync(NotifyMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(connectTimeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new NotifyUnavailableException($"The notify pipe {PipeName} is not available.", ex);
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(replyTimeout);
        NotifyMessage? reply;
        try
        {
            await NotifyProtocol.WriteAsync(pipe, request with { Id = id }, wait.Token);
            reply = await NotifyProtocol.ReadAsync(pipe, wait.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new NotifyUnavailableException($"The notify pipe {PipeName} did not answer in time.", ex);
        }
        catch (IOException ex)
        {
            throw new NotifyUnavailableException($"The notify pipe {PipeName} closed: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidDataException($"The notify pipe sent a reply this app cannot read: {ex.Message}", ex);
        }
        return reply ?? throw new NotifyUnavailableException($"The notify pipe {PipeName} closed without an answer.");
    }
}

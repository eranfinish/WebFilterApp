using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Services;

/// <summary>Where the admin app stands with the service's admin pipe.</summary>
public enum AdminConnectionState
{
    /// <summary>No request has been sent yet.</summary>
    NotConnected,
    Connecting,
    Connected,
    /// <summary>The pipe does not exist or nobody answers it: the service is stopped, not installed, or hung.</summary>
    ServiceNotRunning,
    /// <summary>The pipe refused this process: the admin app must run elevated (or as the service account).</summary>
    NotElevated,
    /// <summary>An open connection ended (service stopped, or it did not answer in time). The next request reconnects.</summary>
    ConnectionLost,
}

/// <summary>
/// Request/reply access to the service's admin pipe (MVP-PLAN §10.2). Every request gets exactly one reply.
/// Implementations connect lazily and reconnect on the next request after a connection is lost.
/// </summary>
public interface IAdminClient
{
    string PipeName { get; }

    AdminConnectionState State { get; }

    /// <summary>Raised when <see cref="State"/> changes; may be raised on any thread.</summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Sends one request and returns the service's reply, which may be an <see cref="AdminError"/>.
    /// Transport problems throw <see cref="AdminConnectionException"/>.
    /// </summary>
    Task<AdminMessage> SendAsync(AdminMessage request, CancellationToken ct = default);
}

public static class AdminClientExtensions
{
    /// <summary>Sends a request and expects a reply of type <typeparamref name="TReply"/>; an <see cref="AdminError"/> throws <see cref="AdminServerException"/>.</summary>
    public static async Task<TReply> RequestAsync<TReply>(this IAdminClient client, AdminMessage request, CancellationToken ct = default) where TReply : AdminMessage
    {
        ArgumentNullException.ThrowIfNull(client);
        var reply = await client.SendAsync(request, ct).ConfigureAwait(false);
        return reply switch
        {
            TReply expected => expected,
            AdminError error => throw new AdminServerException(error.Code, error.Message),
            _ => throw new AdminServerException(AdminServerException.UnexpectedReply,
                $"Expected {typeof(TReply).Name} in reply to {request.GetType().Name}, got {reply.GetType().Name}."),
        };
    }

    /// <summary>Sends a command whose success reply is <see cref="AdminOk"/>.</summary>
    public static Task<AdminOk> ExecuteAsync(this IAdminClient client, AdminMessage request, CancellationToken ct = default) =>
        client.RequestAsync<AdminOk>(request, ct);
}

/// <summary>Base of every failure the admin client reports.</summary>
public abstract class AdminClientException(string message, Exception? innerException = null) : Exception(message, innerException);

public enum AdminConnectionError
{
    ServiceNotRunning,
    NotElevated,
    ConnectionLost,
    Timeout,
}

/// <summary>The request could not be delivered or answered.</summary>
public sealed class AdminConnectionException(AdminConnectionError error, string message, Exception? innerException = null)
    : AdminClientException(message, innerException)
{
    public AdminConnectionError Error { get; } = error;
}

/// <summary>The service answered with an <see cref="AdminError"/> (or with something unexpected).</summary>
public sealed class AdminServerException(string code, string message) : AdminClientException(message)
{
    /// <summary>Client-side code for a reply of the wrong type.</summary>
    public const string UnexpectedReply = "unexpected_reply";

    public string Code { get; } = code;
}

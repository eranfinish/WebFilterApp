using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Services;

/// <summary>
/// Talks to <c>\\.\pipe\WebFilter.Admin</c> with <see cref="AdminProtocol"/> framing. Requests carry unique ids and may
/// overlap; a background reader matches each reply to its request by id. The connection is opened on the first request
/// and reopened on the next request after it ends, so callers never manage it.
/// </summary>
/// <remarks>
/// The service refuses unelevated clients in two ways: the pipe's DACL denies the open (access denied), or it answers the
/// first request with a <c>not_elevated</c> <see cref="AdminError"/> (without an id if no request came) and closes. Both end in
/// <see cref="AdminConnectionState.NotElevated"/>.
/// Requests are never retried once they may have reached the service; only a write that failed on a pipe that had
/// already broken is retried, once, on a fresh connection.
/// </remarks>
public sealed class AdminClient : IAdminClient, IDisposable
{
    public const string NotElevatedCode = "not_elevated";

    /// <summary>How long a failed write waits for the reader to report why the pipe broke.</summary>
    private static readonly TimeSpan BrokenPipeGrace = TimeSpan.FromMilliseconds(500);

    private readonly AdminClientOptions _options;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private Connection? _connection;
    private long _nextId;
    private int _state = (int)AdminConnectionState.NotConnected;

    public AdminClient(AdminClientOptions? options = null) => _options = options ?? new AdminClientOptions();

    public string PipeName => _options.PipeName;

    public AdminConnectionState State => (AdminConnectionState)Volatile.Read(ref _state);

    public event EventHandler? StateChanged;

    public async Task<AdminMessage> SendAsync(AdminMessage request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_closing.IsCancellationRequested, this);
        var id = $"admin-{Interlocked.Increment(ref _nextId)}";
        var message = request with { Id = id };
        var (connection, pending) = await DeliverAsync(message, ct).ConfigureAwait(false);

        AdminMessage reply;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(_options.RequestTimeout);
            try
            {
                reply = await pending.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The service is hung or gone without closing the pipe: start over on the next request.
                connection.Forget(id);
                End(connection, AdminConnectionState.ConnectionLost, Failure(AdminConnectionError.ConnectionLost, "The service stopped answering."));
                throw Failure(AdminConnectionError.Timeout, $"No reply to {request.GetType().Name} within {_options.RequestTimeout.TotalSeconds:0.#} s.");
            }
            catch (OperationCanceledException)
            {
                connection.Forget(id);
                throw;
            }
        }

        if (reply is AdminError { Code: NotElevatedCode } refused)
        {
            End(connection, AdminConnectionState.NotElevated, Failure(AdminConnectionError.NotElevated, refused.Message));
            throw Failure(AdminConnectionError.NotElevated, refused.Message);
        }
        return reply;
    }

    public void Dispose()
    {
        if (_closing.IsCancellationRequested) return;
        _closing.Cancel();
        if (Interlocked.Exchange(ref _connection, null) is { } connection)
            connection.End(Failure(AdminConnectionError.ConnectionLost, "The admin client was closed."));
    }

    /// <summary>Writes the request on the current (or a new) connection; returns the task that completes with its reply.</summary>
    private async Task<(Connection Connection, Task<AdminMessage> Reply)> DeliverAsync(AdminMessage message, CancellationToken ct)
    {
        var id = message.Id!;
        for (var attempt = 1; ; attempt++)
        {
            var connection = await ConnectAsync(ct).ConfigureAwait(false);
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var reply = connection.Expect(id);
                // Never cancel mid-frame: a torn frame would desynchronize the stream for every later request.
                await AdminProtocol.WriteAsync(connection.Pipe, message, CancellationToken.None).ConfigureAwait(false);
                return (connection, reply);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                connection.Forget(id);
                var reason = await connection.WhenEnded(BrokenPipeGrace).ConfigureAwait(false);
                if (reason is { Error: AdminConnectionError.NotElevated })
                {
                    End(connection, AdminConnectionState.NotElevated, reason);
                    throw Failure(AdminConnectionError.NotElevated, reason.Message, ex);
                }
                End(connection, AdminConnectionState.ConnectionLost, reason ?? Failure(AdminConnectionError.ConnectionLost, ex.Message, ex));
                if (attempt >= 2) throw Failure(AdminConnectionError.ConnectionLost, "The connection to the service broke.", ex);
                // The pipe had broken before this request went out (e.g. the service restarted): retry once.
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }

    private async Task<Connection> ConnectAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _connection) is { IsOpen: true } open) return open;
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_closing.IsCancellationRequested, this);
            if (_connection is { IsOpen: true } again) return again;

            var before = State;
            SetState(AdminConnectionState.Connecting);
            var pipe = new NamedPipeClientStream(".", _options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync((int)_options.ConnectTimeout.TotalMilliseconds, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                SetState(AdminConnectionState.NotElevated);
                throw Failure(AdminConnectionError.NotElevated, "The admin pipe refused this process; run the admin app as administrator.", ex);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                SetState(AdminConnectionState.ServiceNotRunning);
                throw Failure(AdminConnectionError.ServiceNotRunning, $"No service answers on pipe '{_options.PipeName}'.", ex);
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                SetState(before);
                throw;
            }

            var connection = new Connection(pipe);
            _connection = connection;
            SetState(AdminConnectionState.Connected);
            _ = Task.Run(() => ReadLoopAsync(connection), CancellationToken.None);
            return connection;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task ReadLoopAsync(Connection connection)
    {
        AdminConnectionException reason;
        var state = AdminConnectionState.ConnectionLost;
        try
        {
            while (true)
            {
                var message = await AdminProtocol.ReadAsync(connection.Pipe, _closing.Token).ConfigureAwait(false);
                if (message is null)
                {
                    reason = Failure(AdminConnectionError.ConnectionLost, "The service closed the connection.");
                    break;
                }
                // A refusal ends the connection as NotElevated whether or not it answers a waiting request; otherwise the
                // hang-up that follows could end it here as ConnectionLost before the request's own End runs.
                if (message is AdminError { Code: NotElevatedCode } refused)
                {
                    connection.Complete(message);
                    reason = Failure(AdminConnectionError.NotElevated, refused.Message);
                    state = AdminConnectionState.NotElevated;
                    break;
                }
                if (connection.Complete(message)) continue;
                // A reply nobody waits for any more (its request was cancelled): ignore it.
            }
        }
        catch (Exception ex)
        {
            // Pipe broken, malformed frame, or the client closing: the connection is over either way.
            reason = Failure(AdminConnectionError.ConnectionLost, ex.Message, ex);
        }
        End(connection, state, reason);
    }

    /// <summary>Ends a connection; if it is still the current one, it is forgotten and the state set.</summary>
    private void End(Connection connection, AdminConnectionState state, AdminConnectionException reason)
    {
        connection.End(reason);
        if (Interlocked.CompareExchange(ref _connection, null, connection) == connection && !_closing.IsCancellationRequested) SetState(state);
    }

    private void SetState(AdminConnectionState state)
    {
        if ((AdminConnectionState)Interlocked.Exchange(ref _state, (int)state) != state) StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static AdminConnectionException Failure(AdminConnectionError error, string message, Exception? inner = null) => new(error, message, inner);

    /// <summary>One open pipe and the requests waiting for a reply on it.</summary>
    private sealed class Connection(NamedPipeClientStream pipe)
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<AdminMessage>> _pending = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource<AdminConnectionException> _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public NamedPipeClientStream Pipe { get; } = pipe;

        public bool IsOpen => !_ended.Task.IsCompleted;

        public Task<AdminMessage> Expect(string id)
        {
            var reply = new TaskCompletionSource<AdminMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = reply;
            if (_ended.Task.IsCompleted && _pending.TryRemove(id, out _)) reply.TrySetException(Copy(_ended.Task.Result));
            return reply.Task;
        }

        public void Forget(string id) => _pending.TryRemove(id, out _);

        public bool Complete(AdminMessage reply) =>
            reply.Id is { } id && _pending.TryRemove(id, out var waiting) && waiting.TrySetResult(reply);

        public async Task<AdminConnectionException?> WhenEnded(TimeSpan grace)
        {
            var finished = await Task.WhenAny(_ended.Task, Task.Delay(grace)).ConfigureAwait(false);
            return finished == _ended.Task ? _ended.Task.Result : null;
        }

        public void End(AdminConnectionException reason)
        {
            if (!_ended.TrySetResult(reason)) return;
            foreach (var id in _pending.Keys)
                if (_pending.TryRemove(id, out var waiting)) waiting.TrySetException(Copy(reason));
            Pipe.Dispose();
        }

        private static AdminConnectionException Copy(AdminConnectionException reason) => new(reason.Error, reason.Message, reason.InnerException);
    }
}

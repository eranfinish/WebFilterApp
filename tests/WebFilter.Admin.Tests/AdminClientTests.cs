using System.IO;
using System.IO.Pipes;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;

namespace WebFilter.Admin.Tests;

/// <summary>The real client against an in-test pipe server speaking <see cref="AdminProtocol"/> (no service involved).</summary>
public sealed class AdminClientTests : IDisposable
{
    private readonly string _pipe = $"WebFilter.Test.Admin.{Guid.NewGuid():N}";
    private readonly List<IDisposable> _cleanup = [];

    public void Dispose()
    {
        foreach (var item in _cleanup) item.Dispose();
    }

    private NamedPipeServerStream Server()
    {
        var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _cleanup.Add(server);
        return server;
    }

    private AdminClient Client(int requestTimeoutMs = 5000, int connectTimeoutMs = 3000)
    {
        var client = new AdminClient(new AdminClientOptions
        {
            PipeName = _pipe,
            ConnectTimeout = TimeSpan.FromMilliseconds(connectTimeoutMs),
            RequestTimeout = TimeSpan.FromMilliseconds(requestTimeoutMs),
        });
        _cleanup.Add(client);
        return client;
    }

    /// <summary>Accepts one connection and answers <paramref name="count"/> requests with "ok: &lt;request type&gt;".</summary>
    private static async Task ServeAsync(NamedPipeServerStream server, int count)
    {
        await server.WaitForConnectionAsync();
        for (var i = 0; i < count; i++)
        {
            var request = await AdminProtocol.ReadAsync(server) ?? throw new EndOfStreamException();
            await AdminProtocol.WriteAsync(server, new AdminOk(request.GetType().Name) with { Id = request.Id });
        }
    }

    [Fact]
    public async Task Replies_are_matched_to_their_requests_by_id()
    {
        var server = Server();
        var client = Client();
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var first = await AdminProtocol.ReadAsync(server);
            var second = await AdminProtocol.ReadAsync(server);
            // Answer out of order: the client must still route each reply to its own request.
            await AdminProtocol.WriteAsync(server, new AdminOk(second!.GetType().Name) with { Id = second.Id });
            await AdminProtocol.WriteAsync(server, new AdminOk(first!.GetType().Name) with { Id = first.Id });
            return (first, second);
        });

        var status = client.SendAsync(new GetStatus());
        var settings = client.SendAsync(new GetSettings());

        Assert.Equal("GetStatus", Assert.IsType<AdminOk>(await status).Detail);
        Assert.Equal("GetSettings", Assert.IsType<AdminOk>(await settings).Detail);
        var (a, b) = await serving;
        Assert.NotNull(a.Id);
        Assert.NotNull(b.Id);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(AdminConnectionState.Connected, client.State);
    }

    [Fact]
    public async Task An_AdminError_reply_is_returned_and_surfaced_as_a_server_exception()
    {
        var server = Server();
        var client = Client();
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            for (var i = 0; i < 2; i++)
            {
                var request = await AdminProtocol.ReadAsync(server);
                await AdminProtocol.WriteAsync(server, new AdminError("bad_request", "Unknown rule 'r-x'") with { Id = request!.Id });
            }
        });

        var raw = await client.SendAsync(new RemoveRule("r-x"));
        var error = await Assert.ThrowsAsync<AdminServerException>(() => client.ExecuteAsync(new RemoveRule("r-x")));

        Assert.Equal(new AdminError("bad_request", "Unknown rule 'r-x'"), raw with { Id = null });
        Assert.Equal("bad_request", error.Code);
        Assert.Equal("Unknown rule 'r-x'", error.Message);
        Assert.Equal(AdminConnectionState.Connected, client.State);
        await serving;
    }

    [Fact]
    public async Task Reconnects_on_the_next_request_after_the_service_drops_the_connection()
    {
        var first = Server();
        var client = Client();
        var serving = ServeAsync(first, 1);
        Assert.IsType<AdminOk>(await client.SendAsync(new GetStatus()));
        await serving;

        first.Dispose(); // the service stops (or restarts)
        await Until(() => client.State == AdminConnectionState.ConnectionLost);

        var second = Server();
        serving = ServeAsync(second, 1);
        var reply = await client.SendAsync(new GetSettings());

        Assert.Equal("GetSettings", Assert.IsType<AdminOk>(reply).Detail);
        Assert.Equal(AdminConnectionState.Connected, client.State);
        await serving;
    }

    [Fact]
    public async Task A_missing_pipe_means_the_service_is_not_running()
    {
        var client = Client(connectTimeoutMs: 200);
        var states = new List<AdminConnectionState>();
        client.StateChanged += (_, _) => states.Add(client.State);

        var error = await Assert.ThrowsAsync<AdminConnectionException>(() => client.SendAsync(new GetStatus()));

        Assert.Equal(AdminConnectionError.ServiceNotRunning, error.Error);
        Assert.Equal(AdminConnectionState.ServiceNotRunning, client.State);
        Assert.Equal([AdminConnectionState.Connecting, AdminConnectionState.ServiceNotRunning], states);
    }

    [Fact]
    public async Task A_service_that_refuses_unelevated_clients_is_reported_as_not_elevated()
    {
        var server = Server();
        var client = Client();
        // What AdminPipeServer does for an unelevated client: answers the first request with not_elevated, then hangs up.
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            var first = await AdminProtocol.ReadAsync(server);
            await AdminProtocol.WriteAsync(server, new AdminError(AdminClient.NotElevatedCode, "Run the admin app as administrator.") { Id = first!.Id });
            server.Dispose();
        });

        var error = await Assert.ThrowsAsync<AdminConnectionException>(() => client.SendAsync(new GetStatus()));

        Assert.Equal(AdminConnectionError.NotElevated, error.Error);
        await Until(() => client.State == AdminConnectionState.NotElevated);
        await serving;
    }

    [Fact]
    public async Task An_unsolicited_refusal_without_an_id_is_also_reported_as_not_elevated()
    {
        var server = Server();
        var client = Client();
        // A client that sends nothing gets the refusal without an id; here the client's request crosses it.
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync();
            await AdminProtocol.WriteAsync(server, new AdminError(AdminClient.NotElevatedCode, "Run the admin app as administrator."));
            try { await AdminProtocol.ReadAsync(server); } catch (IOException) { }
            server.Dispose();
        });

        var error = await Assert.ThrowsAsync<AdminConnectionException>(() => client.SendAsync(new GetStatus()));

        Assert.Equal(AdminConnectionError.NotElevated, error.Error);
        await Until(() => client.State == AdminConnectionState.NotElevated);
        await serving;
    }

    [Fact]
    public async Task A_service_that_does_not_answer_times_out_and_the_next_request_starts_over()
    {
        var silent = Server();
        var client = Client(requestTimeoutMs: 300);
        var swallowing = Task.Run(async () =>
        {
            await silent.WaitForConnectionAsync();
            await AdminProtocol.ReadAsync(silent); // read and never answer
        });

        var error = await Assert.ThrowsAsync<AdminConnectionException>(() => client.SendAsync(new GetStatus()));
        Assert.Equal(AdminConnectionError.Timeout, error.Error);
        Assert.Equal(AdminConnectionState.ConnectionLost, client.State);
        await swallowing;

        var healthy = Server();
        var serving = ServeAsync(healthy, 1);
        Assert.IsType<AdminOk>(await client.SendAsync(new GetStatus()));
        await serving;
    }

    [Fact]
    public async Task A_closed_client_refuses_new_requests()
    {
        var client = Client();
        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.SendAsync(new GetStatus()));
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(20);
        }
    }
}

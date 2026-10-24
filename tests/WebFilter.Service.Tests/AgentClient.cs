using System.Collections.Concurrent;
using System.IO.Pipes;
using WebFilter.Contracts.Agent;

namespace WebFilter.Service.Tests;

/// <summary>Plays the native host on an agent pipe: sends requests and collects replies and pushes, in the order they came.</summary>
internal sealed class AgentClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly ConcurrentQueue<AgentMessage> _received = new();
    private readonly Task _reader;
    private int _next;

    private AgentClient(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = Task.Run(async () =>
        {
            try { while (await AgentProtocol.ReadAsync(_pipe) is { } m) _received.Enqueue(m); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException) { }
        });
    }

    public static async Task<AgentClient> ConnectAsync(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        return new AgentClient(pipe);
    }

    public async Task<T> RequestAsync<T>(AgentMessage message) where T : AgentMessage
    {
        var id = $"t{Interlocked.Increment(ref _next)}";
        await AgentProtocol.WriteAsync(_pipe, message with { Id = id });
        var reply = await WaitForAsync<AgentMessage>(m => m.Id == id);
        return reply as T ?? throw new Xunit.Sdk.XunitException($"Expected {typeof(T).Name}, got {reply?.GetType().Name}: {(reply as ErrorMessage)?.Message}");
    }

    public int CountOf<T>() where T : AgentMessage => _received.OfType<T>().Count();

    /// <summary>Everything received so far, in order.</summary>
    public IReadOnlyList<AgentMessage> Received => [.. _received];

    public Task SendRawAsync(string json) => Contracts.Framing.MessageFraming.WriteAsync(_pipe, System.Text.Encoding.UTF8.GetBytes(json));

    public Task<T?> WaitForAsync<T>(Func<T, bool>? match = null, int timeoutMs = 5000) where T : AgentMessage => Poll(() =>
        _received.OfType<T>().FirstOrDefault(m => match?.Invoke(m) ?? true), timeoutMs);

    public async ValueTask DisposeAsync()
    {
        await _pipe.DisposeAsync();
        await _reader;
    }

    private static async Task<T?> Poll<T>(Func<T?> probe, int timeoutMs) where T : class
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (probe() is { } value) return value;
            await Task.Delay(20);
        }
        return null;
    }
}

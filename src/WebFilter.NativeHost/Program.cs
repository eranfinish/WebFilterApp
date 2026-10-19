using System.IO.Pipes;
using WebFilter.Contracts.Framing;
using WebFilter.NativeHost;

// Native messaging host, launched by Edge/Chrome as the signed-in user (MVP-PLAN §4.3). Relays length-prefixed JSON
// between the browser (stdin/stdout) and the service's agent pipe; answers by itself when the service is unreachable.
var settings = HostSettings.Load();
var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();

await using var pipe = new NamedPipeClientStream(".", settings.AgentPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
try
{
    await pipe.ConnectAsync(1500);
    if (!PipeServerCheck.IsTrusted(pipe, settings))
        throw new UnauthorizedAccessException("The agent pipe is not served by the WebFilter service.");
}
catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
{
    // Answers by itself until the service is reachable again, then exits: the extension reconnects (fail-closed
    // meanwhile) and the new host relays to the service.
    await new Fallback(settings).RunAsync(stdin, stdout);
    return 0;
}

using var cts = new CancellationTokenSource();
var toService = Task.Run(async () =>
{
    while (await MessageFraming.ReadAsync(stdin, cts.Token) is { } frame) await MessageFraming.WriteAsync(pipe, frame, cts.Token);
});
var toBrowser = Task.Run(async () =>
{
    var writer = new BrowserWriter(stdout);
    while (await MessageFraming.ReadAsync(pipe, cts.Token) is { } frame) await writer.WriteAsync(frame, cts.Token);
});
try
{
    await Task.WhenAny(toService, toBrowser);
}
finally
{
    await cts.CancelAsync();
}
// Either side closing ends the host; the extension reconnects (and falls back to fail-closed meanwhile).
return 0;

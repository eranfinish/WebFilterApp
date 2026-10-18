using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using WebFilter.Spike;

// Spike native messaging host: started by the browser as the signed-in user.
// Relays frames between stdin/stdout and the agent pipe; answers on its own
// when the service is not reachable.
const string PipeName = "WebFilter.Spike.Agent";
var logPath = Path.Combine(Path.GetTempPath(), "wf-spike-host.log");
void Log(string message) =>
    File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");

var stdin = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
using var cts = new CancellationTokenSource();
Log($"started, caller={string.Join(' ', args)}");

using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
try
{
    pipe.Connect(1500);
}
catch (TimeoutException)
{
    Log("service unavailable, answering locally");
    await AnswerLocallyAsync();
    return 0;
}

Log("connected to service");
var toService = Task.Run(async () =>
{
    while (await Framing.ReadAsync(stdin, cts.Token) is { } frame) await Framing.WriteAsync(pipe, frame, cts.Token);
});
var toBrowser = Task.Run(async () =>
{
    while (await Framing.ReadAsync(pipe, cts.Token) is { } frame) await Framing.WriteAsync(stdout, frame, cts.Token);
});
await Task.WhenAny(toService, toBrowser);
Log(toService.IsCompleted ? "browser closed the port" : "service closed the pipe");
cts.Cancel();
return 0;

async Task AnswerLocallyAsync()
{
    while (await Framing.ReadAsync(stdin, cts.Token) is { } frame)
    {
        using var request = JsonDocument.Parse(frame);
        var type = request.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
        object reply = type == "hello"
            ? new { type = "hello", source = "host", serviceAvailable = false, account = AccountInfo() }
            : new { type = "error", error = "service_unavailable" };
        await Framing.WriteAsync(stdout, JsonSerializer.SerializeToUtf8Bytes(reply), cts.Token);
    }
}

static object AccountInfo()
{
    using var identity = WindowsIdentity.GetCurrent();
    var elevation = Native.ElevationType(identity.Token);
    var inAdminRole = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    // A split (limited) token means the account is an administrator running unelevated, which is how parents browse.
    var isAdmin = elevation is "limited" or "full" || inAdminRole;
    return new { sid = identity.User?.Value, name = identity.Name, elevation, inAdminRole, isAdmin };
}

static class Native
{
    private const int TokenElevationTypeClass = 18;

    public static string ElevationType(IntPtr token) =>
        GetTokenInformation(token, TokenElevationTypeClass, out var value, sizeof(int), out _)
            ? value switch { 1 => "default", 2 => "full", 3 => "limited", _ => "unknown" }
            : "unknown";

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);
}

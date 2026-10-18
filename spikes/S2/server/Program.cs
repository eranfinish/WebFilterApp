using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using WebFilter.Spike;

// Spike agent pipe server. Identifies each client by the OS (process id,
// image path, session, impersonated SID) and refuses clients whose image is
// not the trusted native host. Writes one JSON event per line to stdout.
const string PipeName = "WebFilter.Spike.Agent";
var trustedHost = args.SkipWhile(a => a != "--trusted").Skip(1).FirstOrDefault();
void Emit(object e) => Console.WriteLine(JsonSerializer.Serialize(e));

var security = new PipeSecurity();
security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

Emit(new { evt = "listening", pipe = PipeName, trustedHost });
while (true)
{
    var server = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    await server.WaitForConnectionAsync();
    _ = Task.Run(() => HandleAsync(server));
}

async Task HandleAsync(NamedPipeServerStream pipe)
{
    using var owned = pipe;
    var client = Identify(pipe);
    var trusted = client.Image is not null && trustedHost is not null && string.Equals(client.Image, trustedHost, StringComparison.OrdinalIgnoreCase);
    Emit(new { evt = "client", client, trusted });

    using var cts = new CancellationTokenSource();
    using var writeLock = new SemaphoreSlim(1, 1);
    Timer? ticker = null;
    var ticks = 0;

    async Task SendAsync(object message)
    {
        await writeLock.WaitAsync();
        try { await Framing.WriteAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(message), cts.Token); }
        finally { writeLock.Release(); }
    }

    try
    {
        while (await Framing.ReadAsync(pipe, cts.Token) is { } frame)
        {
            var message = JsonNode.Parse(frame)!.AsObject();
            if (!trusted)
            {
                await SendAsync(new { type = "rejected", reason = "untrusted_client", image = client.Image });
                Emit(new { evt = "rejected", client.Pid, client.Image });
                break;
            }
            switch ((string?)message["type"])
            {
                case "hello":
                    await SendAsync(new
                    {
                        type = "hello", source = "service", serverPid = Environment.ProcessId,
                        user = new { sid = client.Sid, name = client.UserName },
                        client = new { pid = client.Pid, image = client.Image, sessionId = client.SessionId, trusted, impersonationError = client.ImpersonationError, processTokenSid = client.ProcessTokenSid },
                    });
                    break;
                case "beat":
                    Emit(new { evt = "beat", boot = (string?)message["boot"], phase = (string?)message["phase"], client.Pid });
                    break;
                case "ping":
                    await SendAsync(new { type = "pong", id = (string?)message["id"] });
                    break;
                case "set-ticks":
                    var seconds = (int?)message["intervalSec"] ?? 0;
                    ticker?.Dispose();
                    ticker = seconds > 0
                        ? new Timer(_ => _ = SendAsync(new { type = "tick", n = Interlocked.Increment(ref ticks), at = DateTimeOffset.Now }), null, seconds * 1000, seconds * 1000)
                        : null;
                    await SendAsync(new { type = "ack", what = "set-ticks", seconds });
                    break;
            }
        }
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
    {
    }
    finally
    {
        ticker?.Dispose();
        cts.Cancel();
        Emit(new { evt = "disconnected", client.Pid });
    }
}

// Two ways to learn who the client is:
//  A) impersonate the pipe client (RunAsClient). Without isolation, the identification-level token leaked into a
//     Timer created later on the same async flow, and TimeZoneInfo's registry read then threw SecurityException.
//     Here it runs on a dedicated thread with ExecutionContext flow suppressed.
//  B) read the token of the client process (by PID) — no impersonation at all.
static ClientInfo Identify(NamedPipeServerStream pipe)
{
    Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid);
    Native.GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session);
    string? sid = null, name = null, error = null;
    using (ExecutionContext.SuppressFlow())
    {
        var thread = new Thread(() =>
        {
            try
            {
                pipe.RunAsClient(() =>
                {
                    using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
                    sid = identity?.User?.Value;
                    name = identity?.Name;
                });
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        });
        thread.Start();
        thread.Join();
    }
    return new ClientInfo((int)pid, Native.ImagePath(pid), (int)session, sid, name, error, Native.ProcessUserSid(pid));
}

record ClientInfo(int Pid, string? Image, int SessionId, string? Sid, string? UserName, string? ImpersonationError, string? ProcessTokenSid);

static class Native
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

    public static string? ImagePath(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string? ProcessUserSid(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User?.Value;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    private const uint TokenQuery = 0x0008;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, char[] buffer, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

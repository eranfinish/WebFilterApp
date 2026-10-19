using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WebFilter.NativeHost;

/// <summary>
/// Refuses to talk to a pipe that someone else created under the service's name: the server process must run as
/// LocalSystem, which a child's program cannot.
/// </summary>
internal static class PipeServerCheck
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    public static bool IsTrusted(NamedPipeClientStream pipe, HostSettings settings)
    {
        if (!settings.RequireSystemServer) return true;
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return false;
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var token)) return false;
        using (token)
        {
            using var identity = new WindowsIdentity(token.DangerousGetHandle());
            return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
}

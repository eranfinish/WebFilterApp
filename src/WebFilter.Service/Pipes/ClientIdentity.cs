using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WebFilter.Service.Pipes;

/// <summary>
/// Who is on the other end of a pipe, as told by the OS — never by the client (CLAUDE.md rule 2).
/// The client process handle is opened at connect time and held for the connection's lifetime, so the PID cannot be
/// reused by another process while we trust it. No impersonation: spike S2 showed RunAsClient leaking into timers.
/// </summary>
public sealed class ClientIdentity : IDisposable
{
    private readonly SafeProcessHandle _process;

    public int ProcessId { get; }
    public int SessionId { get; }
    public string? ImagePath { get; }
    public string? UserSid { get; }
    public string? UserName { get; }
    /// <summary>"default", "limited" (admin with a split token, unelevated) or "full".</summary>
    public string Elevation { get; }

    /// <summary>An administrator account, elevated or not.</summary>
    public bool IsAdminAccount => Elevation is "limited" or "full";

    private ClientIdentity(SafeProcessHandle process, int pid, int session, string? image, string? sid, string? name, string elevation)
    {
        _process = process;
        (ProcessId, SessionId, ImagePath, UserSid, UserName, Elevation) = (pid, session, image, sid, name, elevation);
    }

    /// <summary>
    /// The access the client's token is opened with: TOKEN_QUERY for the user and the elevation type, and TOKEN_DUPLICATE
    /// because <see cref="WindowsPrincipal.IsInRole(WindowsBuiltInRole)"/> duplicates the token to check a group. With
    /// TOKEN_QUERY alone it throws "Access is denied" for every account without a split token — every standard account,
    /// so no child's extension could reach the service (the lab, 2026-10-24). LocalSystem may open users' tokens this way.
    /// </summary>
    internal const uint TokenAccess = Native.TokenQuery | Native.TokenDuplicate;

    public static ClientIdentity Of(NamedPipeServerStream pipe)
    {
        if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) throw new IOException("Cannot read the pipe client's process id");
        Native.GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session);
        var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid) throw new IOException($"Cannot open the pipe client's process {pid}");

        string? sid = null, name = null;
        var elevation = "unknown";
        if (Native.OpenProcessToken(process, TokenAccess, out var token))
        {
            using (token)
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                sid = identity.User?.Value;
                name = identity.Name;
                if (Native.GetTokenInformation(token, Native.TokenElevationTypeClass, out var type, sizeof(int), out _))
                    elevation = type switch { 1 => "default", 2 => "full", 3 => "limited", _ => "unknown" };
                // No split token: a standard account, or an administrator with UAC off (then the full token is the only one).
                if (elevation == "default" && IsAdministrator(identity)) elevation = "full";
            }
        }
        return new ClientIdentity(process, (int)pid, (int)session, Native.ImagePath(process), sid, name, elevation);
    }

    /// <summary>The token of <paramref name="process"/> with <see cref="TokenAccess"/>, or null (for tests and <see cref="Of"/>).</summary>
    internal static SafeAccessTokenHandle? OpenToken(SafeProcessHandle process) =>
        Native.OpenProcessToken(process, TokenAccess, out var token) ? token : null;

    internal static bool IsAdministrator(WindowsIdentity identity) => new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

    public void Dispose() => _process.Dispose();

    public override string ToString() => $"pid {ProcessId} session {SessionId} {UserName} ({UserSid}) {Elevation} {ImagePath}";

    private static class Native
    {
        public const uint ProcessQueryLimitedInformation = 0x1000;
        public const uint TokenQuery = 0x0008;
        public const uint TokenDuplicate = 0x0002;
        public const int TokenElevationTypeClass = 18;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int info, int length, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, char[] buffer, ref int size);

        public static string? ImagePath(SafeProcessHandle process)
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
    }
}

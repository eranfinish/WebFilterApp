using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace WebFilter.Service.Guard;

/// <summary>A running process: the account it runs as (its token's user SID), its image and its identity (id and start time).</summary>
public sealed record RunningProgram(string UserSid, string Path, int ProcessId, DateTimeOffset StartedAt);

/// <summary>Finds running processes by program path: the extension guard's view of the browsers (PROTECTION-SPEC §14).</summary>
public interface IRunningPrograms
{
    /// <summary>Every running process, in any session, whose image is one of <paramref name="paths"/> (compared without case).</summary>
    IReadOnlyList<RunningProgram> Find(IReadOnlyCollection<string> paths);
}

/// <summary>
/// Reads the processes with the paths' file names (the system's process list, as Task Manager has it), then for each one, through
/// a handle opened with PROCESS_QUERY_LIMITED_INFORMATION: its full image path (QueryFullProcessImageName, so a chrome.exe
/// elsewhere is not the machine-wide one), its start time (GetProcessTimes) and its token's user. As LocalSystem it sees every
/// user's processes. Read-only: it never changes or ends a process. One that exits meanwhile or cannot be opened is skipped.
/// </summary>
public sealed class WindowsRunningPrograms : IRunningPrograms
{
    public IReadOnlyList<RunningProgram> Find(IReadOnlyCollection<string> paths)
    {
        var wanted = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<RunningProgram>();
        foreach (var name in wanted.Select(Path.GetFileNameWithoutExtension).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                    if (Describe(process.Id) is { } program && wanted.Contains(program.Path)) result.Add(program);
            }
        }
        return result;
    }

    private static RunningProgram? Describe(int processId)
    {
        using var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid) return null;
        if (Native.ImagePath(process) is not { } path || !Native.GetProcessTimes(process, out var created, out _, out _, out _)) return null;
        if (!Native.OpenProcessToken(process, Native.TokenQuery, out var token)) return null;
        using (token)
            return Native.UserSid(token) is { } sid ? new RunningProgram(sid, path, processId, DateTimeOffset.FromFileTime(created)) : null;
    }

    private static class Native
    {
        public const uint ProcessQueryLimitedInformation = 0x1000;
        public const uint TokenQuery = 0x0008;
        private const int TokenUser = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, int length, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, char[] buffer, ref int size);

        public static string? ImagePath(SafeProcessHandle process)
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }

        /// <summary>TOKEN_USER starts with the user's SID_AND_ATTRIBUTES, whose first field points at the SID.</summary>
        public static string? UserSid(SafeAccessTokenHandle token)
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out var length);
            if (length <= 0) return null;
            var buffer = Marshal.AllocHGlobal(length);
            try
            {
                return GetTokenInformation(token, TokenUser, buffer, length, out _) ? new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}

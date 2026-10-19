using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WebFilter.Service.Policies;

namespace WebFilter.Service.Setup;

/// <summary>Loads the registry hive of a user who is not logged on. Uninstall only.</summary>
public interface IUserHives
{
    /// <summary>Loads the user's hive under HKEY_USERS and returns the subkey name it was loaded as, or null if it cannot be loaded.</summary>
    string? Load(string userSid);
}

/// <summary>
/// An <see cref="IPolicyStore"/> that also reaches users who are not logged on, by loading their hive the first time it is
/// needed. Only uninstall uses it; the running service skips such users until their next logon.
/// </summary>
public sealed class HiveLoadingPolicyStore(IPolicyStore inner, IUserHives hives) : IPolicyStore
{
    private readonly Dictionary<string, string?> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The name to use under HKEY_USERS: the SID itself while the user is logged on, otherwise the loaded copy.</summary>
    private string? Resolve(PolicyHive hive, string? userSid)
    {
        if (hive == PolicyHive.Machine || inner.IsAvailable(hive, userSid)) return userSid;
        if (!_loaded.TryGetValue(userSid!, out var name)) _loaded[userSid!] = name = hives.Load(userSid!);
        return name;
    }

    public bool IsAvailable(PolicyHive hive, string? userSid) => hive == PolicyHive.Machine || Resolve(hive, userSid) is not null;

    public object? Get(PolicyHive hive, string? userSid, string keyPath, string name) => inner.Get(hive, Resolve(hive, userSid) ?? userSid, keyPath, name);

    public void Set(PolicyHive hive, string? userSid, string keyPath, string name, object value) => inner.Set(hive, Resolve(hive, userSid) ?? userSid, keyPath, name, value);

    public void Delete(PolicyHive hive, string? userSid, string keyPath, string name) => inner.Delete(hive, Resolve(hive, userSid) ?? userSid, keyPath, name);
}

/// <summary>
/// Loads a user's NTUSER.DAT under <c>HKEY_USERS\WebFilter.Cleanup.&lt;SID&gt;</c> — not under the SID itself, so a logon
/// during uninstall still gets its own hive — and unloads it on dispose. Needs SeBackupPrivilege and SeRestorePrivilege,
/// which LocalSystem (the installer's custom actions) holds.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryUserHives(TextWriter log) : IUserHives, IDisposable
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private readonly List<string> _loaded = [];
    private bool _privileged;

    public string? Load(string userSid)
    {
        try { _ = new SecurityIdentifier(userSid); }
        catch (ArgumentException) { return null; }

        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var profile = machine.OpenSubKey($@"{ProfileList}\{userSid}");
        if (profile?.GetValue("ProfileImagePath") is not string directory)
        {
            log.WriteLine($"No profile folder for {userSid}; its policies cannot be removed.");
            return null;
        }
        var file = Path.Combine(Environment.ExpandEnvironmentVariables(directory), "NTUSER.DAT");
        try
        {
            if (!_privileged)
            {
                Native.EnablePrivilege("SeBackupPrivilege");
                Native.EnablePrivilege("SeRestorePrivilege");
                _privileged = true;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
        {
            log.WriteLine($"Cannot load other users' registry ({ex.Message}); the policies of {userSid} stay.");
            return null;
        }

        var name = "WebFilter.Cleanup." + userSid;
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        var error = Native.RegLoadKey(users.Handle, name, file);
        if (error != 0)
        {
            log.WriteLine($"Could not load the registry of {userSid} from {file}: {new Win32Exception(error).Message}");
            return null;
        }
        _loaded.Add(name);
        return name;
    }

    public void Dispose()
    {
        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        foreach (var name in _loaded)
        {
            var error = Native.RegUnLoadKey(users.Handle, name);
            if (error != 0) log.WriteLine($@"Could not unload HKEY_USERS\{name}: {new Win32Exception(error).Message}");
        }
        _loaded.Clear();
    }

    private static class Native
    {
        private const uint TokenAdjustPrivileges = 0x0020, TokenQuery = 0x0008, SePrivilegeEnabled = 0x0002;
        private const int ErrorNotAllAssigned = 1300;

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivileges { public uint PrivilegeCount; public Luid Luid; public uint Attributes; }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegLoadKeyW")]
        public static extern int RegLoadKey(SafeRegistryHandle key, string subKey, string file);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegUnLoadKeyW")]
        public static extern int RegUnLoadKey(SafeRegistryHandle key, string subKey);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeAccessTokenHandle token);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
        private static extern bool LookupPrivilegeValue(string? systemName, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges newState, int length, IntPtr previousState, IntPtr returnLength);

        /// <summary>Enables a privilege the process token holds; throws if it does not hold it.</summary>
        public static void EnablePrivilege(string name)
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token)) throw new Win32Exception();
            using (token)
            {
                if (!LookupPrivilegeValue(null, name, out var luid)) throw new Win32Exception();
                var state = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SePrivilegeEnabled };
                if (!AdjustTokenPrivileges(token, false, ref state, Marshal.SizeOf<TokenPrivileges>(), IntPtr.Zero, IntPtr.Zero)) throw new Win32Exception();
                if (Marshal.GetLastWin32Error() == ErrorNotAllAssigned) throw new UnauthorizedAccessException($"This process does not hold {name}.");
            }
        }
    }
}

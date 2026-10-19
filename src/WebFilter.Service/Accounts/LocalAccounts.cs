using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using WebFilter.Core.Model;

namespace WebFilter.Service.Accounts;

/// <summary>This computer's user accounts, for linking them to family members (MVP-PLAN §4.5: setup wizard, devices).</summary>
public interface ILocalAccounts
{
    /// <summary>Enabled local user accounts; <see cref="DeviceAccount.MemberId"/> is always null.</summary>
    IReadOnlyList<DeviceAccount> List();
}

/// <summary>
/// Reads the local SAM with <c>NetUserEnum</c> (level 1: name, privilege, flags), which any user may call — nothing is
/// written. Disabled accounts (Guest, DefaultAccount, WDAGUtilityAccount, a disabled Administrator) are left out. The
/// account id is the SID, as the pipes identify clients by their token's SID.
/// </summary>
public sealed class WindowsLocalAccounts : ILocalAccounts
{
    public IReadOnlyList<DeviceAccount> List()
    {
        var accounts = new List<DeviceAccount>();
        var resume = 0u;
        int status;
        do
        {
            status = Native.NetUserEnum(null, 1, Native.FilterNormalAccount, out var buffer, Native.MaxPreferredLength, out var read, out _, ref resume);
            try
            {
                if (status is not (Native.Success or Native.MoreData)) throw new Win32Exception(status, "NetUserEnum failed");
                var size = Marshal.SizeOf<Native.UserInfo1>();
                for (var i = 0; i < read; i++)
                {
                    var info = Marshal.PtrToStructure<Native.UserInfo1>(buffer + i * size);
                    if ((info.Flags & Native.AccountDisable) != 0 || string.IsNullOrEmpty(info.Name)) continue;
                    if (Sid(info.Name) is not { } sid) continue;
                    accounts.Add(new DeviceAccount(sid, info.Name, null, info.Privilege == Native.PrivilegeAdmin));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero) Native.NetApiBufferFree(buffer);
            }
        }
        while (status == Native.MoreData);
        return accounts;
    }

    private static string? Sid(string name)
    {
        try
        {
            return new NTAccount(Environment.MachineName, name).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    private static class Native
    {
        public const int Success = 0;
        public const int MoreData = 234;
        public const uint FilterNormalAccount = 0x2;
        public const uint MaxPreferredLength = uint.MaxValue;
        public const uint AccountDisable = 0x2;
        public const uint PrivilegeAdmin = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct UserInfo1
        {
            public string Name;
            public string Password;
            public uint PasswordAge;
            public uint Privilege;
            public string HomeDir;
            public string Comment;
            public uint Flags;
            public string ScriptPath;
        }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        public static extern int NetUserEnum(string? serverName, uint level, uint filter, out IntPtr buffer, uint preferredMaxLength,
            out uint entriesRead, out uint totalEntries, ref uint resumeHandle);

        [DllImport("netapi32.dll")]
        public static extern int NetApiBufferFree(IntPtr buffer);
    }
}

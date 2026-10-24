using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WebFilter.NativeHost;

/// <summary>
/// Refuses to talk to a pipe that someone else created under the service's name. The pipe's owner must be LocalSystem
/// or BUILTIN\Administrators (the owner LocalSystem gives the objects it creates): a standard account can make neither
/// the owner of a pipe it creates, so a child's program cannot pose as the service.
/// </summary>
/// <remarks>
/// It used to open the server process's token and check its user, which a standard account may not do for a LocalSystem
/// process: every child's host refused the real service and answered by itself (the lab, 2026-10-24). The owner is read
/// from the pipe handle the client already holds (READ_CONTROL comes with read access), so it works for every account.
/// </remarks>
internal static class PipeServerCheck
{
    private const int SeKernelObject = 6;
    private const uint OwnerSecurityInformation = 0x1;

    public static bool IsTrusted(PipeStream pipe, HostSettings settings) =>
        !settings.RequireSystemServer || IsTrustedOwner(OwnerOf(pipe));

    internal static bool IsTrustedOwner(SecurityIdentifier? owner) =>
        owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));

    /// <summary>The owner of the pipe's security descriptor, or null when it cannot be read.</summary>
    internal static SecurityIdentifier? OwnerOf(PipeStream pipe)
    {
        if (GetSecurityInfo(pipe.SafePipeHandle, SeKernelObject, OwnerSecurityInformation, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor) != 0)
            return null;
        try
        {
            return owner == IntPtr.Zero ? null : new SecurityIdentifier(owner);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafeHandle handle, int objectType, uint securityInfo, out IntPtr owner, IntPtr group,
        IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

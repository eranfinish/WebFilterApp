using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WebFilter.Core.Engine;

namespace WebFilter.Service.Network;

/// <summary>
/// <see cref="IWfpEngine"/> over <c>WebFilter.Wfp.Native.dll</c> (src/WebFilter.Wfp.Native, its API in WfpApi.h). Only
/// strings, string arrays and error codes cross the boundary: no WFP structure is declared in C# (CLAUDE.md rule 5).
/// The DLL is loaded only from the service's own folder. Each call opens its own WFP session and needs LocalSystem or an
/// administrator; a failure throws <see cref="Win32Exception"/> with the Win32 or FWP_E_* code.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeWfpEngine : IWfpEngine
{
    public const string LibraryName = "WebFilter.Wfp.Native.dll";

    private const int ErrorInsufficientBuffer = 122;

    public IReadOnlyCollection<string> FilteredUsers()
    {
        var size = 1024;
        while (true)
        {
            var buffer = new char[size];
            var result = Native.WfListUsers(buffer, (uint)buffer.Length, out var required);
            if (result == ErrorInsufficientBuffer && required > size)
            {
                size = checked((int)required);
                continue;
            }
            Check(result, "list the filtered accounts");
            return ParseMultiString(buffer);
        }
    }

    public IReadOnlyList<SkippedApp> SetUserPolicy(UserNetworkPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var paths = policy.AllowedPaths.ToArray();
        var packages = policy.AllowedPackages.ToArray();
        var pathErrors = new uint[paths.Length];
        var packageErrors = new uint[packages.Length];
        var result = Native.WfSetUserPolicy(policy.UserSid, paths, (uint)paths.Length, pathErrors, packages, (uint)packages.Length, packageErrors, policy.TimeUp);
        Check(result, $"set the network filters of {policy.UserSid}");
        return Skipped(paths, pathErrors).Concat(Skipped(packages, packageErrors)).ToList();
    }

    public void RemoveUser(string userSid) => Check(Native.WfRemoveUser(userSid), $"remove the network filters of {userSid}");

    public void RemoveAll() => Check(Native.WfRemoveAll(), "remove the WebFilter provider, sublayer and filters");

    /// <summary>Whether our provider and sublayer exist, and how many filters belong to them (diagnostics).</summary>
    public (bool Provider, bool Sublayer, int Filters) Status()
    {
        Check(Native.WfGetStatus(out var provider, out var sublayer, out var filters), "read the WFP state");
        return (provider, sublayer, checked((int)filters));
    }

    /// <summary>Splits a double-NUL-terminated list ("a\0b\0\0").</summary>
    internal static List<string> ParseMultiString(ReadOnlySpan<char> buffer)
    {
        var values = new List<string>();
        while (buffer.Length > 0 && buffer[0] != '\0')
        {
            var end = buffer.IndexOf('\0');
            if (end < 0) end = buffer.Length;
            values.Add(buffer[..end].ToString());
            buffer = buffer[Math.Min(end + 1, buffer.Length)..];
        }
        return values;
    }

    private static IEnumerable<SkippedApp> Skipped(string[] values, uint[] errors) =>
        values.Zip(errors).Where(e => e.Second != 0).Select(e => new SkippedApp(e.First, unchecked((int)e.Second)));

    private static void Check(uint result, string action)
    {
        if (result == 0) return;
        var code = unchecked((int)result);
        throw new Win32Exception(code, string.Create(CultureInfo.InvariantCulture, $"WFP: could not {action}: {new Win32Exception(code).Message} (0x{result:X8})"));
    }

    private static class Native
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        [DllImport(LibraryName, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint WfSetUserPolicy(string userSid,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] appPaths, uint appPathCount, [Out] uint[] appPathErrors,
            [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] packages, uint packageCount, [Out] uint[] packageErrors,
            [MarshalAs(UnmanagedType.Bool)] bool timeUp);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        [DllImport(LibraryName, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint WfRemoveUser(string userSid);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        [DllImport(LibraryName, CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint WfListUsers([Out] char[] buffer, uint bufferChars, out uint requiredChars);

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        [DllImport(LibraryName, ExactSpelling = true)]
        public static extern uint WfRemoveAll();

        [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
        [DllImport(LibraryName, ExactSpelling = true)]
        public static extern uint WfGetStatus([MarshalAs(UnmanagedType.Bool)] out bool providerPresent, [MarshalAs(UnmanagedType.Bool)] out bool sublayerPresent, out uint filterCount);
    }
}

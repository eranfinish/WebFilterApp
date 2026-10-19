using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WebFilter.Service.Policies;

/// <summary>
/// The real registry (64-bit view): HKLM for machine policies, HKEY_USERS\&lt;SID&gt; for a user's. Writing either needs
/// LocalSystem or an elevated administrator. Empty policy keys left behind by deletions are removed.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryPolicyStore : IPolicyStore
{
    private static RegistryKey Base(PolicyHive hive) =>
        RegistryKey.OpenBaseKey(hive == PolicyHive.Machine ? RegistryHive.LocalMachine : RegistryHive.Users, RegistryView.Registry64);

    private static string FullPath(PolicyHive hive, string? sid, string keyPath) => hive == PolicyHive.Machine ? keyPath : $@"{sid}\{keyPath}";

    public bool IsAvailable(PolicyHive hive, string? userSid)
    {
        if (hive == PolicyHive.Machine) return true;
        using var root = Base(hive);
        using var user = root.OpenSubKey(userSid!);
        return user is not null;
    }

    public object? Get(PolicyHive hive, string? userSid, string keyPath, string name)
    {
        using var root = Base(hive);
        using var key = root.OpenSubKey(FullPath(hive, userSid, keyPath));
        return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
    }

    public void Set(PolicyHive hive, string? userSid, string keyPath, string name, object value)
    {
        using var root = Base(hive);
        using var key = root.CreateSubKey(FullPath(hive, userSid, keyPath), writable: true);
        key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
    }

    public void Delete(PolicyHive hive, string? userSid, string keyPath, string name)
    {
        using var root = Base(hive);
        var path = FullPath(hive, userSid, keyPath);
        using (var key = root.OpenSubKey(path, writable: true))
        {
            if (key is null) return;
            key.DeleteValue(name, throwOnMissingValue: false);
            if (key.ValueCount > 0 || key.SubKeyCount > 0) return;
        }
        // Only the leaf key we used (e.g. ...\ExtensionInstallForcelist or ...\NativeMessagingHosts\com.webfilter.host);
        // shared parents such as SOFTWARE\Policies\Google\Chrome stay.
        if (keyPath.EndsWith(@"\ExtensionInstallForcelist", StringComparison.Ordinal) ||
            keyPath.EndsWith(@"\ExtensionInstallBlocklist", StringComparison.Ordinal) ||
            keyPath.EndsWith(@"\" + BrowserPolicies.HostName, StringComparison.Ordinal))
            root.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}

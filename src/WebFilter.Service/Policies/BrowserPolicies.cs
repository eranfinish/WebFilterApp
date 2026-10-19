using WebFilter.Core.Model;

namespace WebFilter.Service.Policies;

/// <summary>Where a policy value lives: the machine hive, or one user's hive (by SID).</summary>
public enum PolicyHive { Machine, User }

/// <summary>One registry value we manage. <see cref="Value"/> is a string (REG_SZ) or an int (REG_DWORD).</summary>
public sealed record PolicyValue(PolicyHive Hive, string? UserSid, string KeyPath, string Name, object Value);

/// <summary>Store ids and update URLs of the published extension (known after spike S1).</summary>
public sealed record ExtensionIds(string? ChromeId, string? EdgeId)
{
    public const string ChromeUpdateUrl = "https://clients2.google.com/service/update2/crx";
    public const string EdgeUpdateUrl = "https://edge.microsoft.com/extensionwebstorebase/v1/crx";
}

/// <summary>
/// The browser policies of MVP-PLAN §5 as data: machine-wide (force-install, no user-level native hosts, native host
/// registration) and per restricted account (incognito/guest/profiles/dev mode/DevTools/extensions/SafeSearch/YouTube).
/// </summary>
public static class BrowserPolicies
{
    public const string HostName = "com.webfilter.host";
    private const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
    private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";

    public static IReadOnlyList<PolicyValue> Desired(FamilyConfig config, string deviceId, ExtensionIds ids, string nativeHostManifestPath)
    {
        var values = new List<PolicyValue>();
        void Machine(string key, string name, object value) => values.Add(new PolicyValue(PolicyHive.Machine, null, key, name, value));

        // Machine-wide: the extension is installed for everyone; for adults it is transparent.
        if (ids.ChromeId is { } chrome) Machine($@"{Chrome}\ExtensionInstallForcelist", "1", $"{chrome};{ExtensionIds.ChromeUpdateUrl}");
        if (ids.EdgeId is { } edge) Machine($@"{Edge}\ExtensionInstallForcelist", "1", $"{edge};{ExtensionIds.EdgeUpdateUrl}");
        Machine(Chrome, "NativeMessagingUserLevelHosts", 0);
        Machine(Edge, "NativeMessagingUserLevelHosts", 0);
        Machine($@"SOFTWARE\Google\Chrome\NativeMessagingHosts\{HostName}", "", nativeHostManifestPath);
        Machine($@"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\{HostName}", "", nativeHostManifestPath);

        var device = config.Devices.FirstOrDefault(d => d.Id == deviceId);
        foreach (var account in device?.Accounts ?? [])
        {
            if (account.IsAdmin) continue;
            var member = account.MemberId is null ? null : config.Members.FirstOrDefault(m => m.Id == account.MemberId);
            // Unlinked non-admin accounts are restricted too (README §5).
            if (member is { IsRestricted: false }) continue;
            void User(string key, string name, object value) => values.Add(new PolicyValue(PolicyHive.User, account.AccountId, key, name, value));

            User(Chrome, "IncognitoModeAvailability", 1);
            User(Edge, "InPrivateModeAvailability", 1);
            foreach (var browser in new[] { Chrome, Edge })
            {
                User(browser, "BrowserGuestModeEnabled", 0);
                User(browser, "BrowserAddPersonEnabled", 0);
                User(browser, "ExtensionDeveloperModeSettings", 1);
                User(browser, "DeveloperToolsAvailability", 2);
                User($@"{browser}\ExtensionInstallBlocklist", "1", "*");
                User(browser, "ForceGoogleSafeSearch", 1);
                User(browser, "ForceYouTubeRestrict", member?.YouTubeMode == YouTubeMode.Unrestricted ? 0 : member?.Role == Role.Child ? 2 : 1);
            }
            User(Edge, "ForceBingSafeSearch", member?.Role == Role.Teen ? 1 : 2);
        }
        return values;
    }
}

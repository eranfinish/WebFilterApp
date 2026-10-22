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
/// registration) and per restricted account (incognito/guest/profiles/dev mode/DevTools/extensions/SafeSearch/YouTube,
/// and PROTECTION-SPEC §5: pop-ups, abusive experiences, intrusive ads, no proxy, Edge strict tracking prevention and no
/// news feed on Edge's new tab page; §11: Edge's sidebar and Chrome's Gemini off when ai-chat is blocked), plus the Windows
/// policy of PROTECTION-SPEC §2 (no Bing results in Start search unless the family allows web search there).
/// </summary>
public static class BrowserPolicies
{
    public const string HostName = "com.webfilter.host";
    private const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
    private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
    public const string Explorer = @"SOFTWARE\Policies\Microsoft\Windows\Explorer";

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
            // The same switch has a different name in each browser (Edge reports an unknown name as an error in edge://policy).
            User(Chrome, "BrowserAddPersonEnabled", 0);
            User(Edge, "BrowserAddProfileEnabled", 0);
            // Chrome only; Edge has no such policy (its pop-up and intrusive-ads settings below cover the same ground).
            User(Chrome, "AbusiveExperienceInterventionEnforce", 1);
            foreach (var browser in new[] { Chrome, Edge })
            {
                User(browser, "BrowserGuestModeEnabled", 0);
                User(browser, "ExtensionDeveloperModeSettings", 1);
                User(browser, "DeveloperToolsAvailability", 2);
                User($@"{browser}\ExtensionInstallBlocklist", "1", "*");
                User(browser, "ForceGoogleSafeSearch", 1);
                User(browser, "ForceYouTubeRestrict", member?.YouTubeMode == YouTubeMode.Unrestricted ? 0 : member?.Role == Role.Child ? 2 : 1);
                // PROTECTION-SPEC §5: no pop-ups or intrusive ads; no proxy around the filter.
                User(browser, "DefaultPopupsSetting", 2);
                User(browser, "AdsSettingForIntrusiveAdsSites", 2);
                User(browser, "ProxyMode", "direct");
            }
            User(Edge, "ForceBingSafeSearch", member?.Role == Role.Teen ? 1 : 2);
            User(Edge, "TrackingPrevention", 3);
            // No Microsoft News feed (celebrities, politics, ads) on Edge's new tab page; the search box and quick links stay.
            User(Edge, "NewTabPageContentEnabled", 0);
            // PROTECTION-SPEC §11: when the member's categories block ai-chat (unlinked accounts: nothing is allowed), the
            // browsers' own AI goes too. Copilot lives in Edge's sidebar (EdgeCopilotEnabled is for Edge on mobile only, and
            // Microsoft365CopilotChatIconEnabled for work profiles only), so the sidebar is hidden; Chrome has a Gemini switch.
            if (member is null || BuiltinRules.BlocksCategory(config, member, BuiltinRules.AiChatCategory))
            {
                User(Edge, "HubsSidebarEnabled", 0);
                User(Chrome, "GeminiSettings", 1);
            }
            // PROTECTION-SPEC §2: Start search stays local unless the family allows web search there.
            if (!config.WindowsFeatures.WebSearchInStart) User(Explorer, "DisableSearchBoxSuggestions", 1);
        }
        return values;
    }
}

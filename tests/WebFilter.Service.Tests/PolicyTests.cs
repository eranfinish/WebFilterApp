using WebFilter.Core.Model;
using WebFilter.Service.Mapping;
using WebFilter.Service.Policies;
using WebFilter.Contracts.Hub;

namespace WebFilter.Service.Tests;

/// <summary>
/// M4 browser policies (MVP-PLAN §5) against an in-memory registry: who gets which values, removal of values no longer
/// wanted, tamper repair, users who are not logged on, and uninstall. The real registry is exercised only on the lab VM.
/// </summary>
public sealed class PolicyTests : IDisposable
{
    private const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
    private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string Noa = "S-1-5-21-2000-1001", Yoav = "S-1-5-21-2000-1002", Dana = "S-1-5-21-2000-1003", Savta = "S-1-5-21-2000-1004", Visitor = "S-1-5-21-2000-1005";
    private static readonly ExtensionIds Ids = new("chromeidchromeidchromeidchromeid", "edgeidedgeidedgeidedgeidedgeided");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-policy-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryPolicyStore _store = new();
    private readonly PolicyManager _manager;
    private FamilyConfig _config = Fixture();

    public PolicyTests() => _manager = new PolicyManager(_store, Path.Combine(_dir, "managed.json"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private IReadOnlyList<PolicyValue> Desired() => BrowserPolicies.Desired(_config, "pc-kids", Ids, @"C:\Program Files\WebFilter\com.webfilter.host.json");

    [Fact]
    public void Machine_policies_force_install_the_extension_and_register_the_host()
    {
        _manager.Apply(Desired());
        Assert.Equal($"{Ids.ChromeId};{ExtensionIds.ChromeUpdateUrl}", _store.Machine($@"{Chrome}\ExtensionInstallForcelist", "1"));
        Assert.Equal($"{Ids.EdgeId};{ExtensionIds.EdgeUpdateUrl}", _store.Machine($@"{Edge}\ExtensionInstallForcelist", "1"));
        Assert.Equal(0, _store.Machine(Chrome, "NativeMessagingUserLevelHosts"));
        Assert.Equal(0, _store.Machine(Edge, "NativeMessagingUserLevelHosts"));
        Assert.Equal(@"C:\Program Files\WebFilter\com.webfilter.host.json", _store.Machine(@"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.webfilter.host", ""));
    }

    [Fact]
    public void Without_store_ids_no_force_install_is_written()
    {
        var values = BrowserPolicies.Desired(_config, "pc-kids", new ExtensionIds(null, null), "m.json");
        Assert.DoesNotContain(values, v => v.KeyPath.EndsWith("ExtensionInstallForcelist", StringComparison.Ordinal));
    }

    [Fact]
    public void Restricted_and_unlinked_accounts_are_locked_down_adults_and_admins_are_not()
    {
        _manager.Apply(Desired());
        foreach (var sid in new[] { Noa, Yoav, Visitor })
        {
            Assert.Equal(1, _store.User(sid, Chrome, "IncognitoModeAvailability"));
            Assert.Equal(1, _store.User(sid, Edge, "InPrivateModeAvailability"));
            Assert.Equal(0, _store.User(sid, Chrome, "BrowserAddPersonEnabled"));
            Assert.Equal(0, _store.User(sid, Edge, "BrowserAddProfileEnabled"));
            Assert.Null(_store.User(sid, Edge, "BrowserAddPersonEnabled"));
            foreach (var browser in new[] { Chrome, Edge })
            {
                Assert.Equal(0, _store.User(sid, browser, "BrowserGuestModeEnabled"));
                Assert.Equal(1, _store.User(sid, browser, "ExtensionDeveloperModeSettings"));
                Assert.Equal(2, _store.User(sid, browser, "DeveloperToolsAvailability"));
                Assert.Equal("*", _store.User(sid, $@"{browser}\ExtensionInstallBlocklist", "1"));
                Assert.Equal(1, _store.User(sid, browser, "ForceGoogleSafeSearch"));
            }
        }
        AssertOnlyBaseline(Dana);  // admin: the baseline's SafeSearch only (PROTECTION-SPEC §13)
        AssertOnlyBaseline(Savta); // unrestricted adult
    }

    [Fact]
    public void SafeSearch_and_YouTube_strength_follow_the_profile()
    {
        _manager.Apply(Desired());
        Assert.Equal(2, _store.User(Noa, Chrome, "ForceYouTubeRestrict"));  // child: strict
        Assert.Equal(1, _store.User(Yoav, Chrome, "ForceYouTubeRestrict")); // teen: moderate
        Assert.Equal(1, _store.User(Visitor, Edge, "ForceYouTubeRestrict"));
        Assert.Equal(2, _store.User(Noa, Edge, "ForceBingSafeSearch"));
        Assert.Equal(1, _store.User(Yoav, Edge, "ForceBingSafeSearch"));
    }

    [Fact]
    public void Lifting_restrictions_removes_exactly_the_values_we_wrote()
    {
        _store.Set(PolicyHive.User, Yoav, Chrome, "SomeoneElsesPolicy", 7);
        _manager.Apply(Desired());
        _config = Fixture(m => m.Id == "yoav" ? m with { FilterMode = FilterMode.Unrestricted } : m);

        var result = _manager.Apply(Desired());

        Assert.True(result.Deleted > 0);
        // An unrestricted member now gets the parents' baseline (PROTECTION-SPEC §13): the restricted set goes, SafeSearch stays.
        Assert.Equal(new[] { "ForceBingSafeSearch", "ForceGoogleSafeSearch", "ForceGoogleSafeSearch", "SomeoneElsesPolicy" }, _store.ValuesOf(Yoav).Select(v => v.Name).Order());
        Assert.Equal(1, _store.User(Noa, Chrome, "IncognitoModeAvailability"));
    }

    [Fact]
    public void A_changed_or_deleted_value_is_restored_and_reported()
    {
        _manager.Apply(Desired());
        _store.Set(PolicyHive.User, Noa, Chrome, "IncognitoModeAvailability", 0);
        _store.Delete(PolicyHive.User, Noa, Edge, "DeveloperToolsAvailability");

        var result = _manager.Apply(Desired());

        Assert.Equal(2, result.Written);
        Assert.Equal(2, result.Repaired.Count);
        Assert.Contains(result.Repaired, r => r.EndsWith(@"Chrome\IncognitoModeAvailability", StringComparison.Ordinal));
        Assert.Contains(result.Repaired, r => r.EndsWith(@"Edge\DeveloperToolsAvailability", StringComparison.Ordinal));
        Assert.Equal([Noa], result.RepairedUsers);
        Assert.Equal(1, _store.User(Noa, Chrome, "IncognitoModeAvailability"));
        Assert.Equal(2, _store.User(Noa, Edge, "DeveloperToolsAvailability"));
        Assert.Equal(0, _manager.Apply(Desired()).Written);
    }

    /// <summary>PROTECTION-SPEC §5: pop-ups, intrusive ads, proxies, and abusive experiences (Chrome) or tracking and the news feed (Edge), per restricted account.</summary>
    [Fact]
    public void Restricted_and_unlinked_accounts_get_the_protection_policies_adults_and_admins_do_not()
    {
        _manager.Apply(Desired());
        foreach (var sid in new[] { Noa, Yoav, Visitor })
        {
            foreach (var browser in new[] { Chrome, Edge })
            {
                Assert.Equal(2, _store.User(sid, browser, "DefaultPopupsSetting"));
                Assert.Equal(2, _store.User(sid, browser, "AdsSettingForIntrusiveAdsSites"));
                Assert.Equal("""{"ProxyMode":"direct"}""", _store.User(sid, browser, "ProxySettings"));
                Assert.Null(_store.User(sid, browser, "ProxyMode"));
            }
            Assert.Equal(1, _store.User(sid, Chrome, "AbusiveExperienceInterventionEnforce"));
            Assert.Null(_store.User(sid, Edge, "AbusiveExperienceInterventionEnforce"));
            Assert.Equal(3, _store.User(sid, Edge, "TrackingPrevention"));
            Assert.Null(_store.User(sid, Chrome, "TrackingPrevention"));
            Assert.Equal(0, _store.User(sid, Edge, "NewTabPageContentEnabled"));
        }
        AssertOnlyBaseline(Dana);
        AssertOnlyBaseline(Savta);
    }

    /// <summary>
    /// PROTECTION-SPEC §11: when a member's categories block ai-chat, Edge's sidebar (where Copilot lives) is hidden and
    /// Chrome's Gemini integration is off. A child in allowlist mode has ai-chat blocked unless a parent allowed it.
    /// </summary>
    [Fact]
    public void Browser_AI_is_off_where_the_ai_chat_category_is_blocked()
    {
        _manager.Apply(Desired());
        foreach (var sid in new[] { Noa, Visitor })
        {
            Assert.Equal(0, _store.User(sid, Edge, "HubsSidebarEnabled"));
            Assert.Equal(1, _store.User(sid, Chrome, "GeminiSettings"));
        }
        Assert.Null(_store.User(Yoav, Edge, "HubsSidebarEnabled")); // a teen: ai-chat is only offered
        Assert.Null(_store.User(Yoav, Chrome, "GeminiSettings"));
        AssertOnlyBaseline(Dana);
        AssertOnlyBaseline(Savta);

        _config.MemberCategories.Add(new MemberCategory("yoav", "ai-chat", RuleAction.Block));
        _config.MemberCategories.Add(new MemberCategory("noa", "ai-chat", RuleAction.Allow));
        _manager.Apply(Desired());

        Assert.Equal(0, _store.User(Yoav, Edge, "HubsSidebarEnabled"));
        Assert.Equal(1, _store.User(Yoav, Chrome, "GeminiSettings"));
        Assert.Null(_store.User(Noa, Edge, "HubsSidebarEnabled"));
        Assert.Null(_store.User(Noa, Chrome, "GeminiSettings"));
    }

    /// <summary>PROTECTION-SPEC §2: no Bing results in Start search unless the family allows web search there.</summary>
    [Fact]
    public void Start_search_stays_local_unless_the_family_allows_web_search_there()
    {
        _manager.Apply(Desired());
        foreach (var sid in new[] { Noa, Yoav, Visitor })
            Assert.Equal(1, _store.User(sid, BrowserPolicies.Explorer, "DisableSearchBoxSuggestions"));
        AssertOnlyBaseline(Dana);

        _config = WithFeatures(_config, new WindowsFeatures(WebSearchInStart: true));
        var result = _manager.Apply(Desired());

        Assert.Equal(3, result.Deleted);
        foreach (var sid in new[] { Noa, Yoav, Visitor })
            Assert.Null(_store.User(sid, BrowserPolicies.Explorer, "DisableSearchBoxSuggestions"));
        Assert.Equal(1, _store.User(Noa, Chrome, "IncognitoModeAvailability"));
    }

    [Fact]
    public void A_user_who_is_not_logged_on_is_handled_once_their_hive_loads()
    {
        _store.Unloaded.Add(Noa);
        var first = _manager.Apply(Desired());
        Assert.True(first.Skipped > 0);
        Assert.Empty(_store.ValuesOf(Noa));

        _store.Unloaded.Remove(Noa);
        _manager.Apply(Desired());
        Assert.Equal(1, _store.User(Noa, Chrome, "IncognitoModeAvailability"));
    }

    [Fact]
    public void Managed_values_are_remembered_across_restarts_and_removed_on_uninstall()
    {
        _store.Set(PolicyHive.Machine, null, Chrome, "SomeoneElsesPolicy", 1);
        _manager.Apply(Desired());
        var restarted = new PolicyManager(_store, Path.Combine(_dir, "managed.json"));

        Assert.True(restarted.RemoveAll() > 0);

        Assert.Equal(new[] { "SomeoneElsesPolicy" }, _store.All.Select(v => v.Name));
    }

    /// <summary>
    /// PROTECTION-SPEC §13: the parents' accounts — administrators linked to an adult, to nobody or even to a filtered
    /// member, and unrestricted members — get the baseline's SafeSearch and nothing else; the children keep theirs.
    /// </summary>
    [Fact]
    public void Parents_get_only_the_baselines_SafeSearch()
    {
        const string Parent2 = "S-1-5-21-2000-1010", TeenAdmin = "S-1-5-21-2000-1011";
        var kids = _config.Devices.FindIndex(d => d.Id == "pc-kids");
        _config.Devices[kids] = _config.Devices[kids] with
        {
            Accounts = [.. _config.Devices[kids].Accounts, new DeviceAccount(Parent2, "parent2", null, IsAdmin: true), new DeviceAccount(TeenAdmin, "yoav-admin", "yoav", IsAdmin: true)],
        };
        _manager.Apply(Desired());

        foreach (var sid in new[] { Dana, Savta, Parent2, TeenAdmin }) AssertOnlyBaseline(sid);
        Assert.Equal(2, _store.User(Noa, Edge, "ForceBingSafeSearch")); // the children's own strength
        Assert.Equal(1, _store.User(Noa, Chrome, "IncognitoModeAvailability"));

        // The other switches are the extension's: they change no policy.
        _config = WithBaseline(_config, new BaselineProtection(BlockAdult: false, JumpGuard: false, BlockAds: false, SafeSearch: true));
        var again = _manager.Apply(Desired());
        Assert.Equal((0, 0), (again.Written, again.Deleted));
    }

    [Fact]
    public void Turning_the_baselines_SafeSearch_off_removes_the_parents_values_only()
    {
        _manager.Apply(Desired());
        _config = WithBaseline(_config, BaselineProtection.Default with { SafeSearch = false });

        var result = _manager.Apply(Desired());

        Assert.Equal(6, result.Deleted); // three each for dana and savta
        Assert.Empty(_store.ValuesOf(Dana));
        Assert.Empty(_store.ValuesOf(Savta));
        Assert.Equal(1, _store.User(Noa, Chrome, "ForceGoogleSafeSearch"));
        Assert.Equal(1, _store.User(Yoav, Edge, "ForceBingSafeSearch"));
        Assert.True(BrowserPolicies.IsParentAccount(_config, new DeviceAccount(Dana, "dana", "dana", IsAdmin: true)));
        Assert.False(BrowserPolicies.IsParentAccount(_config, new DeviceAccount(Visitor, "visitor", null, IsAdmin: false)));
    }

    /// <summary>Exactly Google SafeSearch in Chrome and Edge and Bing SafeSearch moderate in Edge.</summary>
    private void AssertOnlyBaseline(string sid)
    {
        Assert.Equal([(Chrome, "ForceGoogleSafeSearch"), (Edge, "ForceBingSafeSearch"), (Edge, "ForceGoogleSafeSearch")],
            _store.ValuesOf(sid).OrderBy(v => v.Key, StringComparer.Ordinal).ThenBy(v => v.Name, StringComparer.Ordinal));
        Assert.Equal(1, _store.User(sid, Chrome, "ForceGoogleSafeSearch"));
        Assert.Equal(1, _store.User(sid, Edge, "ForceGoogleSafeSearch"));
        Assert.Equal(1, _store.User(sid, Edge, "ForceBingSafeSearch"));
    }

    internal static FamilyConfig WithFeatures(FamilyConfig c, WindowsFeatures features) => new()
    {
        TimeZone = c.TimeZone, Members = c.Members, Devices = c.Devices, Categories = c.Categories, MemberCategories = c.MemberCategories,
        Rules = c.Rules, Schedules = c.Schedules, DailyLimits = c.DailyLimits, YouTubeChannels = c.YouTubeChannels,
        SupportedBrowsers = c.SupportedBrowsers, AppRules = c.AppRules, WindowsFeatures = features, Baseline = c.Baseline,
    };

    internal static FamilyConfig WithBaseline(FamilyConfig c, BaselineProtection baseline) => new()
    {
        TimeZone = c.TimeZone, Members = c.Members, Devices = c.Devices, Categories = c.Categories, MemberCategories = c.MemberCategories,
        Rules = c.Rules, Schedules = c.Schedules, DailyLimits = c.DailyLimits, YouTubeChannels = c.YouTubeChannels,
        SupportedBrowsers = c.SupportedBrowsers, AppRules = c.AppRules, WindowsFeatures = c.WindowsFeatures, Baseline = baseline,
    };

    internal static FamilyConfig Fixture(Func<FamilyMember, FamilyMember>? patch = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "WebFilter.slnx"))) root = root.Parent!;
        var config = BundleMapper.ToConfig(FamilyBundleJson.Deserialize(File.ReadAllText(Path.Combine(root.FullName, "tests", "simulation", "fixtures", "family-base.json"))));
        if (patch is not null)
            for (var i = 0; i < config.Members.Count; i++) config.Members[i] = patch(config.Members[i]);
        return config;
    }
}

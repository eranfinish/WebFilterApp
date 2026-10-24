using System.Text.Json.Nodes;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;

namespace WebFilter.Core.Tests.Engine;

/// <summary>MVP-PLAN §5.3: which programs each account on pc-kids may reach the network with.</summary>
public class NetworkPolicyTests
{
    private const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    private const string Edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
    private const string School = @"C:\Program Files\SchoolApp\schoolapp.exe";
    private const string Minecraft = @"C:\Games\Minecraft\minecraft.exe";
    private const string Noa = "S-1-5-21-2000-1001", Yoav = "S-1-5-21-2000-1002", Visitor = "S-1-5-21-2000-1005";
    private const string Svchost = @"C:\Windows\System32\svchost.exe";
    private const string OneDrive = @"C:\Program Files\Microsoft OneDrive\OneDrive.exe", OneDriveX86 = @"C:\Program Files (x86)\Microsoft OneDrive\OneDrive.exe";
    private static readonly DateTimeOffset InWindow = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00");  // Monday
    private static readonly DateTimeOffset Morning = DateTimeOffset.Parse("2026-11-02T10:00:00+02:00");   // outside both windows

    /// <summary>The Windows features' programs every restricted account gets with the defaults (PROTECTION-SPEC §2).</summary>
    private static readonly string[] Windows = [Svchost, OneDrive, OneDriveX86];

    private static IReadOnlyList<UserNetworkPolicy> Desired(HubCore hub, DateTimeOffset now) => NetworkPolicy.Desired(hub.View(), "pc-kids", now);

    private static IEnumerable<string> Expected(params string[] paths) => paths.Concat(Windows).Order(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Restricted_and_unlinked_accounts_get_an_allowlist_adults_and_admins_none()
    {
        var policies = Desired(new HubCore(Fixture()), InWindow);
        Assert.Equal(new[] { Noa, Yoav, Visitor }.Order(), policies.Select(p => p.UserSid).Order());
    }

    [Fact]
    public void In_the_window_approved_apps_join_the_browsers()
    {
        var policies = Desired(new HubCore(Fixture()), InWindow);
        var yoav = policies.Single(p => p.UserSid == Yoav);
        Assert.False(yoav.TimeUp);
        Assert.Equal(Expected(Chrome, Edge, Minecraft, School), yoav.AllowedPaths);
        Assert.Equal(["Microsoft.MinecraftUWP_8wekyb3d8bbwe"], yoav.AllowedPackages);

        var noa = policies.Single(p => p.UserSid == Noa);
        Assert.Equal(Expected(Chrome, Edge, School), noa.AllowedPaths);
        Assert.Empty(noa.AllowedPackages);
    }

    [Fact]
    public void When_time_is_up_only_browsers_always_allowed_apps_and_the_windows_features_remain()
    {
        var yoav = Desired(new HubCore(Fixture()), Morning).Single(p => p.UserSid == Yoav);
        Assert.True(yoav.TimeUp);
        Assert.Equal(Expected(Chrome, Edge, School), yoav.AllowedPaths);
        Assert.Empty(yoav.AllowedPackages);
    }

    [Fact]
    public void Windows_features_follow_the_family_setting_for_restricted_and_unlinked_accounts()
    {
        var off = Fixture(new WindowsFeatures(NotificationsAndSync: false, OneDrive: false));
        Assert.All(Desired(new HubCore(off), InWindow), p => Assert.DoesNotContain(p.AllowedPaths, path => Windows.Contains(path)));

        var onlySync = Desired(new HubCore(Fixture(new WindowsFeatures(NotificationsAndSync: true, OneDrive: false))), Morning);
        Assert.All(onlySync, p =>
        {
            Assert.Contains(Svchost, p.AllowedPaths);
            Assert.DoesNotContain(OneDrive, p.AllowedPaths);
        });

        // Admins and unrestricted adults still get no filters at all.
        Assert.Equal(new[] { Noa, Yoav, Visitor }.Order(), onlySync.Select(p => p.UserSid).Order());
    }

    [Fact]
    public void Only_the_machine_wide_OneDrive_the_service_found_is_allowed_never_a_per_user_one()
    {
        var programs = new WindowsPrograms(@"D:\Win\System32\svchost.exe", [@"D:\Programs\Microsoft OneDrive\OneDrive.exe"], WindowsPrograms.Default.MachineBrowsers);
        var noa = NetworkPolicy.Desired(new HubCore(Fixture()).View(), "pc-kids", InWindow, programs).Single(p => p.UserSid == Noa);

        Assert.Contains(@"D:\Win\System32\svchost.exe", noa.AllowedPaths);
        Assert.Contains(@"D:\Programs\Microsoft OneDrive\OneDrive.exe", noa.AllowedPaths);
        Assert.DoesNotContain(OneDrive, noa.AllowedPaths);
        Assert.All(WindowsPrograms.Default.MachineOneDrive.Concat(WindowsPrograms.Default.MachineBrowsers), p => Assert.StartsWith(@"C:\Program Files", p));
        Assert.DoesNotContain(Desired(new HubCore(Fixture()), InWindow).SelectMany(p => p.AllowedPaths),
            p => p.Contains("AppData", StringComparison.OrdinalIgnoreCase) || p.Contains("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_machine_wide_Chrome_and_Edge_are_allowed_when_the_family_lists_no_browsers()
    {
        // A new hub's settings list no browsers; the children must still browse (the extension filters there).
        var config = Fixture(WindowsFeatures.Default, supportedBrowsers: []);
        foreach (var now in new[] { InWindow, Morning })
        {
            var policies = Desired(new HubCore(config), now);
            Assert.Equal(new[] { Noa, Yoav, Visitor }.Order(), policies.Select(p => p.UserSid).Order());
            Assert.All(policies, p => Assert.Superset(new HashSet<string>([Chrome, Edge]), p.AllowedPaths.ToHashSet()));
        }

        var programs = WindowsPrograms.Default with { MachineBrowsers = [@"D:\Programs\Google\Chrome\Application\chrome.exe"] };
        var noa = NetworkPolicy.Desired(new HubCore(config).View(), "pc-kids", InWindow, programs).Single(p => p.UserSid == Noa);
        Assert.Contains(@"D:\Programs\Google\Chrome\Application\chrome.exe", noa.AllowedPaths);
        Assert.DoesNotContain(Edge, noa.AllowedPaths);
    }

    [Fact]
    public void A_member_block_beats_a_global_allow_and_expired_rules_are_ignored()
    {
        var config = Fixture();
        config.AppRules.Add(new AppRule("g-chat", null, AppMatchType.Path, @"C:\Apps\chat.exe", RuleAction.Allow));
        config.AppRules.Add(new AppRule("y-chat", "yoav", AppMatchType.Path, @"C:\Apps\chat.exe", RuleAction.Block));
        config.AppRules.Add(new AppRule("y-old", "yoav", AppMatchType.Path, @"C:\Apps\old.exe", RuleAction.Allow, ExpiresAt: InWindow.AddMinutes(-1)));
        var policies = Desired(new HubCore(config), InWindow);
        Assert.DoesNotContain(@"C:\Apps\chat.exe", policies.Single(p => p.UserSid == Yoav).AllowedPaths);
        Assert.DoesNotContain(@"C:\Apps\old.exe", policies.Single(p => p.UserSid == Yoav).AllowedPaths);
        Assert.Contains(@"C:\Apps\chat.exe", policies.Single(p => p.UserSid == Noa).AllowedPaths);
    }

    [Fact]
    public void A_paused_member_has_no_filters_until_the_pause_ends()
    {
        var hub = new HubCore(Fixture());
        hub.Pause("yoav", Morning.AddHours(1), Morning);
        Assert.DoesNotContain(Desired(hub, Morning), p => p.UserSid == Yoav);
        Assert.Contains(Desired(hub, Morning.AddHours(2)), p => p.UserSid == Yoav);
    }

    /// <summary>
    /// PROTECTION-SPEC §14: a browser the extension guard blocks leaves that account's allowlist — every copy of it, from the
    /// machine-wide paths and from the family's supported browsers — also when time is up, and the rest stays as it was.
    /// </summary>
    [Fact]
    public void A_browser_the_extension_guard_blocks_leaves_that_accounts_allowlist_until_it_is_lifted()
    {
        var hub = new HubCore(Fixture());
        const string ChromeX86 = @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe";
        var programs = WindowsPrograms.Default with { MachineBrowsers = [Chrome, ChromeX86, Edge] };
        AccountBrowser[] noaChrome = [new(Noa.ToLowerInvariant(), BrowserKind.Chrome)];

        foreach (var now in new[] { InWindow, Morning })
        {
            var free = NetworkPolicy.Desired(hub.View(), "pc-kids", now, programs);
            var guarded = NetworkPolicy.Desired(hub.View(), "pc-kids", now, programs, noaChrome);

            var noa = guarded.Single(p => p.UserSid == Noa);
            var before = free.Single(p => p.UserSid == Noa);
            Assert.Contains(ChromeX86, before.AllowedPaths);
            Assert.Equal(before.AllowedPaths.Where(p => p is not (Chrome or ChromeX86)), noa.AllowedPaths);
            Assert.Contains(Edge, noa.AllowedPaths);
            Assert.Equal(before.TimeUp, noa.TimeUp);
            Assert.Equal(before.AllowedPackages, noa.AllowedPackages);
            Assert.All(free.Where(p => p.UserSid != Noa), p => Assert.True(p.Equivalent(guarded.Single(g => g.UserSid == p.UserSid))));
        }

        // Lifted: the full allowlist again. An unlinked account's browser is blocked the same way; an administrator has no filters to change.
        Assert.Contains(Chrome, NetworkPolicy.Desired(hub.View(), "pc-kids", InWindow, programs, []).Single(p => p.UserSid == Noa).AllowedPaths);
        var visitor = NetworkPolicy.Desired(hub.View(), "pc-kids", InWindow, programs, [new AccountBrowser(Visitor, BrowserKind.Edge)]).Single(p => p.UserSid == Visitor);
        Assert.DoesNotContain(Edge, visitor.AllowedPaths);
        Assert.Contains(Chrome, visitor.AllowedPaths);
        Assert.Equal(new[] { Noa, Yoav, Visitor }.Order(),
            NetworkPolicy.Desired(hub.View(), "pc-kids", InWindow, programs, [new AccountBrowser("S-1-5-21-2000-1003", BrowserKind.Chrome)]).Select(p => p.UserSid).Order());
    }

    [Fact]
    public void The_filtered_accounts_are_the_ones_that_get_a_policy()
    {
        var hub = new HubCore(Fixture());
        hub.Pause("noa", Morning.AddHours(1), Morning);
        foreach (var now in new[] { Morning, Morning.AddHours(2) })
            Assert.Equal(Desired(hub, now).Select(p => p.UserSid), NetworkPolicy.FilteredAccounts(hub.View(), "pc-kids", now).Select(a => a.AccountId));
        Assert.DoesNotContain(NetworkPolicy.FilteredAccounts(hub.View(), "pc-kids", Morning), a => a.AccountId == Noa);
        Assert.Empty(NetworkPolicy.FilteredAccounts(hub.View(), "no-such-device", Morning));
    }

    [Fact]
    public void Equivalent_ignores_case_and_detects_changes()
    {
        var a = new UserNetworkPolicy("S", false, [@"C:\A.exe"], []);
        Assert.True(a.Equivalent(new UserNetworkPolicy("S", false, [@"c:\a.EXE"], [])));
        Assert.False(a.Equivalent(a with { TimeUp = true }));
        Assert.False(a.Equivalent(a with { AllowedPaths = [@"C:\A.exe", @"C:\B.exe"] }));
    }

    private static FamilyConfig Fixture() =>
        FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);

    private static FamilyConfig Fixture(WindowsFeatures features, List<string>? supportedBrowsers = null)
    {
        var c = Fixture();
        return new FamilyConfig
        {
            TimeZone = c.TimeZone, Members = c.Members, Devices = c.Devices, Categories = c.Categories, MemberCategories = c.MemberCategories,
            Rules = c.Rules, Schedules = c.Schedules, DailyLimits = c.DailyLimits, YouTubeChannels = c.YouTubeChannels,
            SupportedBrowsers = supportedBrowsers ?? c.SupportedBrowsers, AppRules = c.AppRules, WindowsFeatures = features,
        };
    }
}

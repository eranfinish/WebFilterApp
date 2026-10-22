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
        var programs = new WindowsPrograms(@"D:\Win\System32\svchost.exe", [@"D:\Programs\Microsoft OneDrive\OneDrive.exe"]);
        var noa = NetworkPolicy.Desired(new HubCore(Fixture()).View(), "pc-kids", InWindow, programs).Single(p => p.UserSid == Noa);

        Assert.Contains(@"D:\Win\System32\svchost.exe", noa.AllowedPaths);
        Assert.Contains(@"D:\Programs\Microsoft OneDrive\OneDrive.exe", noa.AllowedPaths);
        Assert.DoesNotContain(OneDrive, noa.AllowedPaths);
        Assert.All(WindowsPrograms.Default.MachineOneDrive, p => Assert.StartsWith(@"C:\Program Files", p));
        Assert.DoesNotContain(Desired(new HubCore(Fixture()), InWindow).SelectMany(p => p.AllowedPaths),
            p => p.Contains("AppData", StringComparison.OrdinalIgnoreCase) || p.Contains("%LOCALAPPDATA%", StringComparison.OrdinalIgnoreCase));
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

    private static FamilyConfig Fixture(WindowsFeatures features)
    {
        var c = Fixture();
        return new FamilyConfig
        {
            TimeZone = c.TimeZone, Members = c.Members, Devices = c.Devices, Categories = c.Categories, MemberCategories = c.MemberCategories,
            Rules = c.Rules, Schedules = c.Schedules, DailyLimits = c.DailyLimits, YouTubeChannels = c.YouTubeChannels,
            SupportedBrowsers = c.SupportedBrowsers, AppRules = c.AppRules, WindowsFeatures = features,
        };
    }
}

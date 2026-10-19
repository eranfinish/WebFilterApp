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
    private static readonly DateTimeOffset InWindow = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00");  // Monday
    private static readonly DateTimeOffset Morning = DateTimeOffset.Parse("2026-11-02T10:00:00+02:00");   // outside both windows

    private static IReadOnlyList<UserNetworkPolicy> Desired(HubCore hub, DateTimeOffset now) => NetworkPolicy.Desired(hub.View(), "pc-kids", now);

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
        Assert.Equal(new[] { Chrome, Edge, Minecraft, School }.Order(StringComparer.OrdinalIgnoreCase), yoav.AllowedPaths);
        Assert.Equal(["Microsoft.MinecraftUWP_8wekyb3d8bbwe"], yoav.AllowedPackages);

        var noa = policies.Single(p => p.UserSid == Noa);
        Assert.Equal(new[] { Chrome, Edge, School }.Order(StringComparer.OrdinalIgnoreCase), noa.AllowedPaths);
        Assert.Empty(noa.AllowedPackages);
    }

    [Fact]
    public void When_time_is_up_only_browsers_and_always_allowed_apps_remain()
    {
        var yoav = Desired(new HubCore(Fixture()), Morning).Single(p => p.UserSid == Yoav);
        Assert.True(yoav.TimeUp);
        Assert.Equal(new[] { Chrome, Edge, School }.Order(StringComparer.OrdinalIgnoreCase), yoav.AllowedPaths);
        Assert.Empty(yoav.AllowedPackages);
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
}

using System.Text.Json.Nodes;
using WebFilter.Core.Decisions;
using WebFilter.Core.Dnr;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;
using WebFilter.Core.Time;
using static WebFilter.Core.Tests.Dnr.DnrTestPolicies;

namespace WebFilter.Core.Tests.Dnr;

/// <summary>
/// The rules the extension gets (<see cref="ExtensionPolicy"/> compiled to DNR) must agree with <see cref="Decider.Url"/>
/// in every member state — normal, paused, homework — so the browser blocks exactly what the service would.
/// </summary>
public class ExtensionPolicyTests
{
    private static readonly DateTimeOffset InWindow = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00"); // Monday, inside both windows
    private static readonly DeviceAccount Noa = new("S-1-5-21-2000-1001", "noa", "noa", false);
    private static readonly DeviceAccount Yoav = new("S-1-5-21-2000-1002", "yoav", "yoav", false);

    private static readonly string[] Urls =
    [
        "https://www.khanacademy.org/math", "https://en.wikipedia.org/wiki/Cat", "https://mashov.info/grades", "https://www.instagram.com/",
        "https://www.roblox.com/games", "https://chess.example/", "https://www.youtube.com/watch?v=abc", "https://youtu.be/abc",
        "https://scratch.mit.edu/projects", "https://mit.edu/", "http://192.168.1.10/", "https://news.example/today",
    ];

    public static TheoryData<string> States() => ["normal", "paused", "homework"];

    [Theory]
    [MemberData(nameof(States))]
    public void Compiled_rules_agree_with_the_service_decision(string state)
    {
        var hub = new HubCore(Fixture());
        foreach (var member in new[] { "noa", "yoav" })
        {
            if (state == "paused") hub.Pause(member, InWindow.AddHours(1), InWindow);
            if (state == "homework") hub.StartHomework(member, 60, InWindow);
        }
        var view = hub.View();

        foreach (var account in new[] { Noa, Yoav })
        {
            var effective = ExtensionPolicy.For(view, account, InWindow);
            var rules = effective.UrlPolicy is null ? [] : DnrCompiler.Compile(effective.UrlPolicy, Options).DynamicRules;
            foreach (var url in Urls)
            {
                var service = Decider.Url(view, account.MemberId!, url, InWindow).Decision == Decision.Allow ? DnrOutcome.Allow : DnrOutcome.Block;
                Assert.True(service == DnrSimulator.Evaluate(rules, url), $"{state} {account.Name} {url}: service {service}");
            }
        }
    }

    [Fact]
    public void Paused_and_homework_turn_time_off_and_say_when_they_end()
    {
        var hub = new HubCore(Fixture());
        hub.Pause("yoav", InWindow.AddMinutes(45), InWindow);
        hub.StartHomework("noa", 30, InWindow);
        var view = hub.View();

        var yoav = ExtensionPolicy.For(view, Yoav, InWindow);
        Assert.Equal((null, YouTubeMode.Unrestricted, false, InWindow.AddMinutes(45)), (yoav.UrlPolicy, yoav.YouTube, yoav.TimeApplies, yoav.ValidUntil));

        var noa = ExtensionPolicy.For(view, Noa, InWindow);
        Assert.Equal((YouTubeMode.Off, false, InWindow.AddMinutes(30)), (noa.YouTube, noa.TimeApplies, noa.ValidUntil));

        var normal = ExtensionPolicy.For(hub.View(), Noa, InWindow.AddMinutes(31));
        Assert.Equal((YouTubeMode.Allowlist, true), (normal.YouTube, normal.TimeApplies));
    }

    public static TheoryData<string> StatesWithTimeUp() => ["normal", "paused", "homework", "time_up"];

    /// <summary>
    /// PROTECTION-SPEC §4 in every member state: noa (child, allowlist, google.com always allowed for school) and yoav
    /// (teen, blocklist) both block image search. While time is up the extension adds its session rules.
    /// </summary>
    [Theory]
    [MemberData(nameof(StatesWithTimeUp))]
    public void Image_search_blocks_agree_with_the_service_decision(string state)
    {
        var config = Fixture();
        foreach (var id in new[] { "noa", "yoav" })
        {
            var index = config.Members.FindIndex(m => m.Id == id);
            config.Members[index] = config.Members[index] with { BlockImageSearch = true };
        }
        config.Rules.Add(new Rule("r-google", "noa", "google.com", PatternType.Domain, RuleAction.Allow, AlwaysAllowed: true));
        var hub = new HubCore(config);
        var now = state == "time_up" ? DateTimeOffset.Parse("2026-11-02T10:00:00+02:00") : InWindow;
        foreach (var member in new[] { "noa", "yoav" })
        {
            if (state == "paused") hub.Pause(member, now.AddHours(1), now);
            if (state == "homework") hub.StartHomework(member, 60, now);
        }
        var view = hub.View();

        foreach (var account in new[] { Noa, Yoav })
        {
            var effective = ExtensionPolicy.For(view, account, now);
            var compiled = effective.UrlPolicy is null ? new DnrRuleSet([], []) : DnrCompiler.Compile(effective.UrlPolicy, Options);
            var timeUp = effective.TimeApplies && Decider.Time(view, account.MemberId!, now).State is Core.Time.TimeState.OutsideWindow or Core.Time.TimeState.Expired;
            IEnumerable<DnrRule> installed = timeUp ? compiled.TimeUpSessionRules.Concat(compiled.DynamicRules) : compiled.DynamicRules;
            foreach (var url in ImageSearchTests.ImageSearchUrls.Concat(ImageSearchTests.OtherUrls).Where(u => !u.Contains("youtube", StringComparison.Ordinal)))
            {
                var decision = Decider.Url(view, account.MemberId!, url, now);
                var service = decision.Decision == Decision.Allow ? DnrOutcome.Allow : DnrOutcome.Block;
                Assert.True(service == DnrSimulator.Evaluate(installed, url), $"{state} {account.Name} {url}: service {decision}");
                if (state != "paused" && ImageSearchTests.ImageSearchUrls.Contains(url)) Assert.Equal(ReasonCode.BlockedImageSearch, decision.Reason);
            }
        }
    }

    [Fact]
    public void Features_follow_the_member_flags_and_the_role_and_are_off_for_everyone_else()
    {
        var config = Fixture();
        var noa = config.Members.FindIndex(m => m.Id == "noa");
        config.Members[noa] = config.Members[noa] with { JumpGuard = true, BlockYouTubeShorts = true, LogSearches = true, BlockImageSearch = true, BlockAds = true };
        var yoav = config.Members.FindIndex(m => m.Id == "yoav");
        config.Members[yoav] = config.Members[yoav] with { JumpGuard = true };
        var savta = config.Members.FindIndex(m => m.Id == "savta");
        config.Members[savta] = config.Members[savta] with { JumpGuard = true, LogSearches = true, BlockAds = true };
        var hub = new HubCore(config);
        var accounts = hub.Config.Device("pc-kids").Accounts;
        ExtensionFeatures Of(string name, DateTimeOffset? at = null) => ExtensionPolicy.For(hub.View(), accounts.Single(a => a.Name == name), at ?? InWindow).Features;

        Assert.Equal(new ExtensionFeatures(JumpGuard: true, BlockYouTubeShorts: true, LogSearches: true, CanOverrideJump: false, BlockAds: true), Of("noa"));
        Assert.Equal(new ExtensionFeatures(JumpGuard: true, BlockYouTubeShorts: false, LogSearches: false, CanOverrideJump: true, BlockAds: false), Of("yoav"));
        Assert.Equal(ExtensionFeatures.None, Of("savta"));   // unrestricted adult: flags ignored
        Assert.Equal(ExtensionFeatures.None, Of("dana"));    // administrator
        Assert.Equal(ExtensionFeatures.None, Of("visitor")); // unlinked

        hub.StartHomework("noa", 30, InWindow);
        Assert.Equal(new ExtensionFeatures(true, true, true, false, true), Of("noa"));
        hub.StopHomework("noa", InWindow);
        // A pause lifts the restrictions (ad blocking included), not the search log the member was told about.
        hub.Pause("noa", InWindow.AddHours(1), InWindow);
        Assert.Equal(new ExtensionFeatures(JumpGuard: false, BlockYouTubeShorts: false, LogSearches: true, CanOverrideJump: false, BlockAds: false), Of("noa"));
        Assert.Equal(new ExtensionFeatures(true, true, true, false, true), Of("noa", InWindow.AddHours(2)));
    }

    /// <summary>
    /// PROTECTION-SPEC §12: ad blocking stays on while the member is filtered — homework and time up included — and is off
    /// for any pause (the hub's or this computer's own), like the other filtering. Children and teens alike.
    /// </summary>
    [Fact]
    public void Ad_blocking_follows_the_flag_while_filtered_and_stops_with_any_pause()
    {
        var config = Fixture();
        foreach (var id in new[] { "noa", "yoav" })
        {
            var i = config.Members.FindIndex(m => m.Id == id);
            config.Members[i] = config.Members[i] with { BlockAds = true };
        }
        var hub = new HubCore(config);
        bool Ads(PolicyView view, DeviceAccount account, DateTimeOffset now) => ExtensionPolicy.For(view, account, now).Features.BlockAds;

        Assert.True(Ads(hub.View(), Noa, InWindow));
        Assert.True(Ads(hub.View(), Yoav, InWindow));
        var night = DateTimeOffset.Parse("2026-11-02T23:30:00+02:00"); // outside noa's window: time is up, the filtering goes on
        Assert.Equal(TimeState.OutsideWindow, Decider.Time(hub.View(), "noa", night).State);
        Assert.True(Ads(hub.View(), Noa, night));

        hub.StartHomework("noa", 30, InWindow);
        Assert.True(Ads(hub.View(), Noa, InWindow));
        hub.StopHomework("noa", InWindow);

        hub.Pause("yoav", InWindow.AddHours(1), InWindow);
        Assert.False(Ads(hub.View(), Yoav, InWindow));
        Assert.True(Ads(hub.View(), Yoav, InWindow.AddHours(2)));

        var local = hub.View() with { LocalPauses = new Dictionary<string, DateTimeOffset> { ["noa"] = InWindow.AddMinutes(30) } };
        Assert.False(Ads(local, Noa, InWindow));
        Assert.True(Ads(local, Noa, InWindow.AddHours(1)));

        hub.SetMember(hub.Config.Member("noa") with { BlockAds = false }, InWindow);
        Assert.False(Ads(hub.View(), Noa, InWindow));
    }

    private static FamilyConfig Fixture() =>
        FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);
}

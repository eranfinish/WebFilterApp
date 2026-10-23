using System.Text.Json.Nodes;
using WebFilter.Core.Decisions;
using WebFilter.Core.Dnr;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Dnr;
using WebFilter.Core.Tests.Simulation;
using static WebFilter.Core.Tests.Dnr.DnrTestPolicies;

namespace WebFilter.Core.Tests.Engine;

/// <summary>
/// PROTECTION-SPEC §13, the parents' baseline protection in the Core: the defaults, whom it covers, the adult block and a
/// parent's allow exception in the evaluator and in the compiled rules, each switch, and that nothing of it is recorded.
/// </summary>
public class BaselineProtectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string[] Adult = ["adult-site.example", "nsfw.example"];

    // pc-kids: noa (child), yoav (teen), dana (administrator, adult member), savta (adult member, standard), visitor (unlinked).
    private static readonly DeviceAccount Parent2 = new("S-1-5-21-2000-1010", "parent2", null, IsAdmin: true);

    private static readonly string[] Urls =
    [
        "https://adult-site.example/", "https://www.adult-site.example/video?id=1", "https://nsfw.example/x", "https://notadult-site.example/",
        "https://adult-site.example/health/clinic", "https://www.instagram.com/", "http://192.168.1.1/", "http://10.0.0.1/admin",
        "https://www.youtube.com/watch?v=abc", "https://youtu.be/abc", "https://mashov.info/", "https://videosite.example/",
    ];

    [Fact]
    public void Missing_means_all_on()
    {
        Assert.Equal(new BaselineProtection(BlockAdult: true, JumpGuard: true, BlockAds: true, SafeSearch: true), BaselineProtection.Default);
        Assert.Equal(BaselineProtection.Default, new FamilyConfig { TimeZone = TimeZoneInfo.Utc }.Baseline);
        Assert.Equal(BaselineProtection.Default, Fixture().Baseline);
        Assert.True(new BaselineProtection(false, false, false, SafeSearch: true).IsOn);
        Assert.False(new BaselineProtection(false, false, false, false).IsOn);
        Assert.Equal("adult", BaselineProtection.AdultCategory);
        Assert.Equal("blocked_baseline_adult", ReasonCode.BlockedBaselineAdult.ToWire());
        Assert.Equal(ReasonCode.BlockedBaselineAdult, ReasonCodes.Parse("blocked_baseline_adult"));
    }

    [Fact]
    public void It_covers_administrators_linked_or_not_and_unrestricted_members_only()
    {
        var config = Fixture();
        var accounts = config.Device("pc-kids").Accounts;
        DeviceAccount Named(string name) => accounts.Single(a => a.Name == name);

        Assert.True(BaselineProtection.AppliesTo(config, Named("dana")));   // administrator, linked to an adult
        Assert.True(BaselineProtection.AppliesTo(config, Named("savta")));  // unrestricted member, standard account
        Assert.True(BaselineProtection.AppliesTo(config, Parent2));         // administrator linked to nobody
        Assert.False(BaselineProtection.AppliesTo(config, Named("noa")));   // child
        Assert.False(BaselineProtection.AppliesTo(config, Named("yoav")));  // teen
        Assert.False(BaselineProtection.AppliesTo(config, Named("visitor"))); // unlinked standard account
        // An administrator account linked to a filtered member is filtered as that member.
        Assert.False(BaselineProtection.AppliesTo(config, Named("yoav") with { IsAdmin = true }));
    }

    [Fact]
    public void Adult_sites_are_blocked_for_parents_everything_else_is_unrestricted()
    {
        var view = new HubCore(Fixture(adult: true)).View();
        foreach (var member in new[] { "dana", "savta" })
        {
            Assert.Equal(DecisionResult.Block(ReasonCode.BlockedBaselineAdult), Decider.Url(view, member, "https://www.adult-site.example/video?id=1", Now));
            Assert.Equal(DecisionResult.Block(ReasonCode.BlockedBaselineAdult), Decider.Url(view, member, "https://nsfw.example/", Now));
            foreach (var open in new[] { "https://notadult-site.example/", "https://www.instagram.com/", "http://192.168.1.1/", "https://www.youtube.com/watch?v=abc" })
                Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedUnrestricted), Decider.Url(view, member, open, Now));
            Assert.Equal(DecisionResult.NotApplicable, Decider.Url(view, member, "ftp://adult-site.example/", Now));
        }
        // An administrator linked to nobody, the same way.
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedBaselineAdult), Decider.Baseline(view, null, "https://adult-site.example/", Now));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedUnrestricted), Decider.Baseline(view, null, "https://example.org/", Now));
        // The children are unchanged: noa sees approved sites only, yoav what his categories do not block.
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedNotInAllowlist), Decider.Url(view, "noa", "https://adult-site.example/", Now));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedDefault), Decider.Url(view, "yoav", "https://adult-site.example/", Now));
    }

    [Fact]
    public void A_parents_allow_rule_wins_and_another_parents_does_not_count()
    {
        var config = Fixture(adult: true);
        config.Rules.Add(new Rule("r-dana", "dana", "nsfw.example", PatternType.Domain, RuleAction.Allow));
        config.Rules.Add(new Rule("r-health", null, "adult-site.example/health", PatternType.Prefix, RuleAction.Allow));
        config.Rules.Add(new Rule("r-gone", "dana", "adult-site.example", PatternType.Domain, RuleAction.Allow, ExpiresAt: Now));
        config.Rules.Add(new Rule("r-dana-block", "dana", "news.example", PatternType.Domain, RuleAction.Block));
        var view = new HubCore(config).View();

        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedRule, "r-dana"), Decider.Url(view, "dana", "https://www.nsfw.example/", Now));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedRule, "r-health"), Decider.Url(view, "dana", "https://adult-site.example/health/clinic", Now));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedRule, "r-health"), Decider.Baseline(view, null, "https://adult-site.example/health", Now));
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedBaselineAdult), Decider.Url(view, "dana", "https://adult-site.example/", Now)); // expired
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedRule, "r-gone"), Decider.Url(view, "dana", "https://adult-site.example/", Now.AddSeconds(-1)));
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedBaselineAdult), Decider.Url(view, "savta", "https://www.nsfw.example/", Now));
        // A parent's own block rules and allow rules outside the list change nothing.
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedUnrestricted), Decider.Url(view, "dana", "https://news.example/", Now));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedUnrestricted), Decider.Url(view, "dana", "https://mashov.info/", Now));
    }

    [Fact]
    public void Without_blockAdult_or_without_the_list_nothing_is_blocked()
    {
        var off = new HubCore(Fixture(adult: true, baseline: BaselineProtection.Default with { BlockAdult = false })).View();
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedUnrestricted), Decider.Url(off, "dana", "https://adult-site.example/", Now));
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedNotInAllowlist), Decider.Url(off, "noa", "https://adult-site.example/", Now));

        var noList = new HubCore(Fixture()).View();
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedUnrestricted), Decider.Url(noList, "dana", "https://adult-site.example/", Now));
        Assert.Empty(EffectiveUrlPolicy.ForBaseline(noList.Config, "dana", Now).CategoryDomains);
        Assert.DoesNotContain(DnrCompiler.Compile(EffectiveUrlPolicy.ForBaseline(noList.Config, "dana", Now), Options).DynamicRules, r => r.Action.Type == "redirect");
    }

    [Fact]
    public void A_pause_does_not_apply_to_parents()
    {
        var hub = new HubCore(Fixture(adult: true));
        hub.Pause("dana", Now.AddHours(2), Now);
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedBaselineAdult), Decider.Url(hub.View(), "dana", "https://adult-site.example/", Now));
        var effective = ExtensionPolicy.For(hub.View(), Account(hub.Config, "dana"), Now);
        Assert.True(effective.Baseline);
        Assert.NotNull(effective.UrlPolicy);
        Assert.True(effective.Features.JumpGuard && effective.Features.BlockAds);
    }

    /// <summary>The extension's rules for a parent: the baseline policy compiled, the switches as features, no time, YouTube unrestricted.</summary>
    [Fact]
    public void The_extension_gets_the_baseline_for_parents()
    {
        var config = Fixture(adult: true);
        config.Rules.Add(new Rule("r-dana", "dana", "nsfw.example", PatternType.Domain, RuleAction.Allow));
        var hub = new HubCore(config);
        var features = new ExtensionFeatures(JumpGuard: true, BlockYouTubeShorts: false, LogSearches: false, CanOverrideJump: true, BlockAds: true);

        foreach (var account in new[] { Account(config, "dana"), Account(config, "savta"), Parent2 })
        {
            var effective = ExtensionPolicy.For(hub.View(), account, Now);
            Assert.True(effective.Baseline, account.Name);
            Assert.Equal((YouTubeMode.Unrestricted, false), (effective.YouTube, effective.TimeApplies));
            Assert.Equal(features, effective.Features);
            var policy = Assert.IsType<EffectiveUrlPolicy>(effective.UrlPolicy);
            Assert.True(policy.Baseline);
            Assert.Equal(FilterMode.Blocklist, policy.Mode);
            Assert.Empty(policy.BuiltinBlocks);
            Assert.Equal(Adult, policy.CategoryDomains.Select(c => c.Domain));
            Assert.All(policy.CategoryDomains, c => Assert.Equal(RuleAction.Block, c.Action));
            Assert.All(policy.MemberRules, r => Assert.Equal(RuleAction.Allow, r.Action));
            Assert.Equal(account.Name == "dana", policy.MemberRules.Any(r => r.Id == "r-dana"));
            Assert.Contains(policy.MemberRules, r => r.Id == "r-school"); // the family's always-allowed rule
        }

        var compiled = DnrCompiler.Compile(ExtensionPolicy.For(hub.View(), Account(config, "dana"), Now).UrlPolicy!, Options);
        Assert.Empty(compiled.TimeUpSessionRules);
        // No structural rules: no default block, no IP-literal gate, no YouTube rules.
        Assert.DoesNotContain(compiled.DynamicRules, r => r.Id is DnrRuleIds.AllowlistDefault or DnrRuleIds.IpLiteralGate or DnrRuleIds.YouTube
            or DnrRuleIds.YouTubeShort or DnrRuleIds.YouTubeSignIn);
        var block = Assert.Single(compiled.DynamicRules, r => r.Action.Type == "redirect");
        Assert.Equal(Adult, block.Condition.RequestDomains);
        Assert.InRange(block.Priority, DnrPriorities.GlobalBand, DnrPriorities.MemberBand - 1);
        Assert.All(compiled.DynamicRules.Where(r => r.Action.Type == "allow"), r => Assert.InRange(r.Priority, DnrPriorities.MemberBand, DnrPriorities.YouTube - 1));
        Assert.All(compiled.DynamicRules, r => Assert.Equal(["main_frame", "sub_frame"], r.Condition.ResourceTypes));
    }

    [Theory]
    [InlineData("blockAdult")]
    [InlineData("jumpGuard")]
    [InlineData("blockAds")]
    [InlineData("safeSearch")]
    [InlineData("all")]
    public void Each_switch_off(string off)
    {
        var baseline = off switch
        {
            "blockAdult" => BaselineProtection.Default with { BlockAdult = false },
            "jumpGuard" => BaselineProtection.Default with { JumpGuard = false },
            "blockAds" => BaselineProtection.Default with { BlockAds = false },
            "safeSearch" => BaselineProtection.Default with { SafeSearch = false },
            _ => new BaselineProtection(false, false, false, false),
        };
        var config = Fixture(adult: true, baseline: baseline);
        var hub = new HubCore(config);
        var effective = ExtensionPolicy.For(hub.View(), Account(config, "dana"), Now);

        Assert.Equal(off != "all", effective.Baseline);
        Assert.Equal(off is not ("blockAdult" or "all"), effective.UrlPolicy is not null);
        Assert.Equal(off is not ("jumpGuard" or "all"), effective.Features.JumpGuard);
        Assert.Equal(off is not ("blockAds" or "all"), effective.Features.BlockAds);
        Assert.Equal(off != "all", effective.Features.CanOverrideJump);
        Assert.False(effective.Features.LogSearches || effective.Features.BlockYouTubeShorts);
        // All off: exactly what an unrestricted account got before the baseline existed.
        if (off == "all") Assert.Equal(new ExtensionPolicy(null, YouTubeMode.Unrestricted, false, DateTimeOffset.MaxValue), effective);
        Assert.Equal(off is "blockAdult" or "all" ? ReasonCode.AllowedUnrestricted : ReasonCode.BlockedBaselineAdult, Decider.Url(hub.View(), "dana", "https://adult-site.example/", Now).Reason);
        // The children's policies never change with these switches.
        var noa = ExtensionPolicy.For(hub.View(), Account(config, "noa"), Now);
        Assert.False(noa.Baseline);
        Assert.Equal(ExtensionFeatures.Of(config.Member("noa")), noa.Features);
    }

    [Fact]
    public void The_compiled_rules_agree_with_the_service_for_parents_and_children()
    {
        var config = Fixture(adult: true);
        config.Rules.Add(new Rule("r-dana", "dana", "nsfw.example", PatternType.Domain, RuleAction.Allow));
        config.Rules.Add(new Rule("r-health", null, "adult-site.example/health", PatternType.Prefix, RuleAction.Allow));
        config.MemberCategories.Add(new MemberCategory("yoav", "adult", RuleAction.Block));
        var hub = new HubCore(config);
        var agent = new AgentCore("pc-kids");
        agent.Apply(hub.Snapshot());

        foreach (var account in config.Device("pc-kids").Accounts.Append(Parent2))
        {
            var effective = ExtensionPolicy.For(agent.View(), account, Now);
            var rules = effective.UrlPolicy is null ? [] : DnrCompiler.Compile(effective.UrlPolicy, Options).DynamicRules;
            foreach (var url in Urls)
            {
                var decision = agent.ExplainUrl(account, url, Now);
                var service = decision.Decision == Decision.Allow ? DnrOutcome.Allow : DnrOutcome.Block;
                Assert.True(service == DnrSimulator.Evaluate(rules, url), $"{account.Name} {url}: service {decision}");
            }
        }
    }

    [Fact]
    public void Nothing_of_a_parents_browsing_is_recorded()
    {
        var hub = new HubCore(Fixture(adult: true));
        var agent = new AgentCore("pc-kids");
        agent.Apply(hub.Snapshot());
        var dana = agent.AccountOf("dana");

        Assert.Equal(ReasonCode.BlockedBaselineAdult, agent.DecideUrl(dana, "https://adult-site.example/", Now).Reason);
        Assert.Equal(ReasonCode.BlockedBaselineAdult, agent.DecideUrl(Parent2, "https://adult-site.example/", Now).Reason);
        Assert.False(agent.ReportBlock(dana, "https://adult-site.example/", ReasonCode.BlockedBaselineAdult, Now));
        Assert.False(agent.ReportBlock(agent.AccountOf("savta"), "https://other.example/", ReasonCode.BlockedJump, Now));
        Assert.False(agent.ReportOverride(dana, "https://other.example/", ReasonCode.BlockedJump, Now));
        Assert.False(agent.ReportOverride(Parent2, "https://other.example/", ReasonCode.BlockedJump, Now));
        Assert.False(agent.ReportSearch(dana, "google", "anything", "https://www.google.com/search?q=anything", Now));
        agent.Heartbeat(dana, Now, active: true);
        Assert.True(agent.IsParent(dana) && agent.IsParent(Parent2) && !agent.IsParent(agent.AccountOf("noa")));
        Assert.Empty(agent.LocalEvents);
        Assert.Equal(Core.Time.TimeState.Unlimited, agent.TimeStatus("dana", Now).State);

        // The children's are, as before.
        Assert.True(agent.ReportOverride(agent.AccountOf("yoav"), "https://other.example/", ReasonCode.BlockedJump, Now));
        Assert.True(agent.ReportBlock(agent.AccountOf("noa"), "https://other.example/", ReasonCode.BlockedJump, Now));
        agent.DecideUrl(agent.AccountOf("noa"), "https://adult-site.example/", Now);
        Assert.Equal([EventType.Override, EventType.Blocked, EventType.Blocked], agent.LocalEvents.Select(e => e.Type));
        hub.Ingest(agent.TakeUpload());
        Assert.DoesNotContain(hub.Events, e => e.MemberId == "dana" || (e.MemberId is null && e.Type == EventType.Blocked));
    }

    [Fact]
    public void An_administrators_youtube_is_never_filtered()
    {
        var hub = new HubCore(Fixture(adult: true));
        var agent = new AgentCore("pc-kids");
        agent.Apply(hub.Snapshot());
        var page = new YouTubePage("https://www.youtube.com/watch?v=vidGamr0001", "UCgamerX0000000000000003");
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedUnrestricted), agent.DecideYouTube(Parent2, page, Now));
        Assert.Equal(DecisionResult.Allow(ReasonCode.AllowedUnrestricted), agent.DecideYouTube(agent.AccountOf("dana"), page, Now));
    }

    [Fact]
    public void The_baseline_policy_is_cached_per_member_until_a_rule_expires()
    {
        var config = Fixture(adult: true);
        config.Rules.Add(new Rule("r-tmp", "dana", "nsfw.example", PatternType.Domain, RuleAction.Allow, ExpiresAt: Now.AddMinutes(10)));
        var cache = new PolicyCache(config);

        var first = cache.GetBaseline("dana", Now);
        Assert.Same(first, cache.GetBaseline("dana", Now.AddMinutes(5)));
        Assert.NotSame(first, cache.GetBaseline(null, Now));
        Assert.NotSame(first, cache.Get("dana", Now));
        var later = cache.GetBaseline("dana", Now.AddMinutes(10));
        Assert.NotSame(first, later);
        Assert.DoesNotContain(later.MemberRules, r => r.Id == "r-tmp");
    }

    private static DeviceAccount Account(FamilyConfig config, string name) => config.Device("pc-kids").Accounts.Single(a => a.Name == name);

    private static FamilyConfig Fixture(bool adult = false, BaselineProtection? baseline = null)
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject();
        var setup = new JsonObject();
        if (adult) setup["categories"] = new JsonObject { ["adult"] = new JsonArray([.. Adult.Select(d => JsonValue.Create(d))]) };
        if (baseline is { } b)
            setup["baseline"] = new JsonObject { ["blockAdult"] = b.BlockAdult, ["jumpGuard"] = b.JumpGuard, ["blockAds"] = b.BlockAds, ["safeSearch"] = b.SafeSearch };
        return FixtureLoader.Load(fixture, setup);
    }
}

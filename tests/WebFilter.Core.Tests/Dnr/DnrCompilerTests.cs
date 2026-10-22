using System.Text.Json;
using System.Text.Json.Nodes;
using WebFilter.Core.Decisions;
using WebFilter.Core.Dnr;
using WebFilter.Core.Model;
using static WebFilter.Core.Tests.Dnr.DnrTestPolicies;

namespace WebFilter.Core.Tests.Dnr;

public class DnrCompilerTests
{
    private const string BlockPagePrefix = $"chrome-extension://{ExtensionId}/blocked.html#";

    private static DnrRuleSet Compile(EffectiveUrlPolicy policy) => DnrCompiler.Compile(policy, Options);

    // --- modes and defaults -------------------------------------------------------------------------------------------

    [Fact]
    public void UnrestrictedMember_GetsNoRules()
    {
        var set = Compile(Policy(FilterMode.Unrestricted, YouTubeMode.Off, [Member("x.example", PatternType.Domain, RuleAction.Block, always: true)]));
        Assert.Empty(set.DynamicRules);
        Assert.Empty(set.TimeUpSessionRules);
    }

    [Fact]
    public void Allowlist_BlocksEverythingNotAllowed_WithALowestPriorityCatchAll()
    {
        var set = Compile(Policy(FilterMode.Allowlist, global: [Global("ok.example", PatternType.Domain, RuleAction.Allow)]));
        var catchAll = Assert.Single(set.DynamicRules, r => r.Id == DnrRuleIds.AllowlistDefault);
        Assert.Equal(1, catchAll.Priority);
        Assert.Equal(set.DynamicRules.Min(r => r.Priority), catchAll.Priority);
        Assert.Null(catchAll.Condition.RequestDomains);
        Assert.Equal(DnrOutcome.Block, Outcome(set, "https://anything.example/"));
        Assert.Equal(DnrOutcome.Block, Outcome(set, "https://anything.example/frame", "sub_frame"));
        Assert.Equal(DnrOutcome.Allow, Outcome(set, "https://www.ok.example/x"));
        Assert.DoesNotContain(set.DynamicRules, r => r.Id == DnrRuleIds.IpLiteralGate);
    }

    [Fact]
    public void Blocklist_AllowsByDefault_AndHasNoCatchAll()
    {
        var set = Compile(Policy(FilterMode.Blocklist));
        Assert.DoesNotContain(set.DynamicRules, r => r.Id == DnrRuleIds.AllowlistDefault);
        Assert.Equal(DnrOutcome.Allow, Outcome(set, "https://anything.example/"));
    }

    [Fact]
    public void EveryRule_FiltersOnlyMainAndSubFrames()
    {
        var set = Compile(Policy(FilterMode.Allowlist, member: [Member("x.example", PatternType.Domain, RuleAction.Block)]));
        Assert.All(set.DynamicRules.Concat(set.TimeUpSessionRules), r => Assert.Equal(["main_frame", "sub_frame"], r.Condition.ResourceTypes));
        Assert.Equal(DnrOutcome.Allow, DnrSimulator.Evaluate(set.DynamicRules, "https://x.example/img.png", "image"));
    }

    /// <summary>
    /// PROTECTION-SPEC §12: the extension's ad-server ruleset blocks scripts, images, requests and frames at the lowest
    /// priority, and that works on an allowed site only while no compiled rule reaches below the frames: no
    /// allowAllRequests (it would lift every block on the pages it matches) and no allow rule for other resource types.
    /// </summary>
    [Theory]
    [InlineData(FilterMode.Allowlist)]
    [InlineData(FilterMode.Blocklist)]
    public void NoRule_LetsSubresourcesThrough_SoAdBlockingStillWorksOnAllowedSites(FilterMode mode)
    {
        var policy = Policy(mode, YouTubeMode.Restricted,
            member:
            [
                Member("news.example", PatternType.Domain, RuleAction.Allow),
                Member("school.example/homework", PatternType.Prefix, RuleAction.Allow, always: true),
                Member("*.edu.example", PatternType.Wildcard, RuleAction.Allow),
                Member(@"^https://games\.example/", PatternType.Regex, RuleAction.Allow),
            ],
            global: [Global("mashov.info", PatternType.Domain, RuleAction.Allow, always: true)],
            categories: [new CategoryDomain("ads.example", RuleAction.Block, "ads-popups"), new CategoryDomain("kids.example", RuleAction.Allow, "educational")])
            with { BuiltinBlocks = [new BuiltinBlock(Global("images.example", PatternType.Domain, RuleAction.Block, id: "builtin-image-search-x"), ReasonCode.BlockedImageSearch)] };

        var set = Compile(policy);

        Assert.All(set.DynamicRules.Concat(set.TimeUpSessionRules), r =>
        {
            Assert.Contains(r.Action.Type, new[] { "allow", "redirect" });
            Assert.Equal(["main_frame", "sub_frame"], r.Condition.ResourceTypes);
        });
        Assert.DoesNotContain("allowAllRequests", JsonSerializer.Serialize(set));
    }

    // --- block page redirect -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("https://www.blocked.example/path/x?q=1&lang=%D7%A2#frag")]
    [InlineData("https://sub.wild.example:8443/a")]
    [InlineData("https://u:p@pre.example/r/funny?sort=new")]
    [InlineData("https://www.bestcasino.example/play")]
    [InlineData("http://3232235777/admin")]
    public void BlockRules_RedirectToTheBlockPage_WithTheExactOriginalUrl(string input)
    {
        var set = Compile(Policy(member:
        [
            Member("blocked.example", PatternType.Domain, RuleAction.Block),
            Member("*.wild.example", PatternType.Wildcard, RuleAction.Block),
            Member("pre.example/r/", PatternType.Prefix, RuleAction.Block),
            Member("casino", PatternType.Regex, RuleAction.Block),
        ]));
        var url = ChromeUrl.Parse(input);
        var rule = DnrSimulator.Match(set.DynamicRules, url);
        Assert.NotNull(rule);
        Assert.Equal("redirect", rule.Action.Type);
        Assert.Equal(BlockPagePrefix + "\\0", rule.Action.Redirect!.RegexSubstitution);
        Assert.Equal(BlockPagePrefix + url.Spec, DnrSimulator.RedirectTarget(rule, url));
    }

    [Fact]
    public void AllowlistCatchAll_AndTimeUpBlock_AlsoCarryTheOriginalUrl()
    {
        var set = Compile(Policy(FilterMode.Allowlist));
        var url = ChromeUrl.Parse("https://some.example/a?b#c");
        Assert.Equal(BlockPagePrefix + url.Spec, DnrSimulator.RedirectTarget(DnrSimulator.Match(set.DynamicRules, url)!, url));
        Assert.Equal(BlockPagePrefix + url.Spec, DnrSimulator.RedirectTarget(DnrSimulator.Match(set.TimeUpSessionRules, url)!, url));
    }

    [Fact]
    public void BlockPagePath_ComesFromTheOptions()
    {
        var set = DnrCompiler.Compile(Policy(FilterMode.Allowlist), new DnrCompileOptions("ext", "/pages/block.html"));
        Assert.Equal("chrome-extension://ext/pages/block.html#\\0", set.DynamicRules.Single(r => r.Id == DnrRuleIds.AllowlistDefault).Action.Redirect!.RegexSubstitution);
        Assert.Throws<ArgumentException>(() => DnrCompiler.Compile(Policy(), new DnrCompileOptions("")));
        Assert.Throws<ArgumentException>(() => DnrCompiler.Compile(Policy(), new DnrCompileOptions("ext", "blocked.html")));
    }

    // --- YouTube -------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> YouTubeUrls => new()
    {
        { "https://www.youtube.com/", "main_frame" },
        { "https://youtube.com/watch?v=vidKhan0001", "main_frame" },
        { "https://m.youtube.com/watch?v=vidKhan0001", "main_frame" },
        { "https://music.youtube.com/watch?v=vidMusc0001", "main_frame" },
        { "https://youtu.be/vidKhan0001", "main_frame" },
        { "https://youtu.be./vidKhan0001", "main_frame" },
        { "https://www.youtube-nocookie.com/embed/vidKhan0001", "sub_frame" },
        { "https://u:p@www.youtube.com/", "main_frame" },
    };

    [Theory]
    [MemberData(nameof(YouTubeUrls))]
    public void YouTubeOff_BlocksEveryYouTubeHost_AboveEveryRule(string url, string frame)
    {
        var allowEverything = new[] { "youtube.com", "youtu.be", "youtube-nocookie.com" }
            .Select(h => Member(h, PatternType.Domain, RuleAction.Allow, always: false)).ToList();
        allowEverything.Add(Member("www.youtube.com/watch", PatternType.Prefix, RuleAction.Allow));
        var set = Compile(Policy(FilterMode.Blocklist, YouTubeMode.Off, allowEverything));
        Assert.Equal(DnrOutcome.Block, Outcome(set, url, frame));
        var rule = DnrSimulator.Match(set.DynamicRules, ChromeUrl.Parse(url), frame)!;
        Assert.Equal(set.DynamicRules.Max(r => r.Priority), rule.Priority);
    }

    [Theory]
    [MemberData(nameof(YouTubeUrls))]
    public void YouTubeOtherModes_AllowEveryYouTubeHost_AboveEveryRule(string url, string frame)
    {
        foreach (var mode in new[] { YouTubeMode.Restricted, YouTubeMode.Allowlist, YouTubeMode.Unrestricted })
        {
            var set = Compile(Policy(FilterMode.Allowlist, mode,
                member: [Member("youtube.com", PatternType.Domain, RuleAction.Block), Member("*.youtube.com", PatternType.Wildcard, RuleAction.Block)],
                global: [Global("youtu.be", PatternType.Domain, RuleAction.Block), Global(@"youtube", PatternType.Regex, RuleAction.Block)]));
            Assert.True(Outcome(set, url, frame) == DnrOutcome.Allow, $"{mode}: {url}");
        }
    }

    [Theory]
    [InlineData("https://youtube.com.example.net/watch?v=x")]
    [InlineData("https://notyoutube.com/watch?v=x")]
    [InlineData("https://www.youtu.be/x")]
    public void YouTubeLookAlikes_FollowTheUrlRules(string url)
    {
        Assert.Equal(DnrOutcome.Allow, Outcome(Compile(Policy(FilterMode.Blocklist, YouTubeMode.Off)), url));
        Assert.Equal(DnrOutcome.Block, Outcome(Compile(Policy(FilterMode.Allowlist, YouTubeMode.Unrestricted)), url));
    }

    // --- IP literals ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(FilterMode.Blocklist)]
    [InlineData(FilterMode.Allowlist)]
    public void IpLiterals_AreBlocked_UnlessAnAllowRuleMatchesThem(FilterMode mode)
    {
        var set = Compile(Policy(mode,
            member: [Member("192.168.1.50", PatternType.Domain, RuleAction.Allow), Member("10.1.1.1/printer", PatternType.Prefix, RuleAction.Allow)],
            global: [Global("[::1]", PatternType.Domain, RuleAction.Allow), Global("www.example.com", PatternType.Domain, RuleAction.Allow)]));

        foreach (var url in new[] { "http://192.168.1.1/", "http://3232235777/", "http://0xC0A80101/", "http://[::2]:8080/", "http://10.1.1.1/admin", "http://u@10.0.0.1:81/x" })
            Assert.True(Outcome(set, url) == DnrOutcome.Block, url);
        foreach (var url in new[] { "http://192.168.1.50/", "http://3232235826/x", "http://[::1]:8080/", "http://[0:0::1]/", "http://10.1.1.1/printer/queue", "https://www.example.com/" })
            Assert.True(Outcome(set, url) == DnrOutcome.Allow, url);
    }

    [Fact]
    public void IpLiteralGate_IsBelowEveryRule_SoAMemberBlockStillBeatsAGlobalAllow()
    {
        var set = Compile(Policy(FilterMode.Blocklist,
            member: [Member("192.168.1.50/admin", PatternType.Prefix, RuleAction.Block)],
            global: [Global("192.168.1.50", PatternType.Domain, RuleAction.Allow)]));
        Assert.Equal(DnrOutcome.Allow, Outcome(set, "http://192.168.1.50/"));
        Assert.Equal(DnrOutcome.Block, Outcome(set, "http://192.168.1.50/admin/x"));
        var gate = set.DynamicRules.Single(r => r.Id == DnrRuleIds.IpLiteralGate);
        Assert.Equal(set.DynamicRules.Min(r => r.Priority), gate.Priority);
        Assert.Equal(DnrOutcome.Allow, Outcome(set, "http://1.2.3.4.example.com/"));
        Assert.Equal(DnrOutcome.Allow, Outcome(set, "http://1.2.3.4:5@example.org/"));
    }

    [Fact]
    public void IpLiteralInAnAllowCategory_DoesNotLiftTheIpBlock()
    {
        var set = Compile(Policy(FilterMode.Blocklist, categories: [new CategoryDomain("192.168.1.50", RuleAction.Allow, "c")]));
        Assert.Equal(DnrOutcome.Block, Outcome(set, "http://192.168.1.50/"));
    }

    // --- rule shapes ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Wildcard_MatchesSubdomainsOnly_EvenWithUserinfoTricksOrATrailingDot()
    {
        var set = Compile(Policy(member: [Member("*.google.com", PatternType.Wildcard, RuleAction.Block)]));
        foreach (var url in new[] { "https://mail.google.com/", "https://a.b.google.com:8443/x", "https://www.google.com./", "https://u:p@www.google.com/" })
            Assert.True(Outcome(set, url) == DnrOutcome.Block, url);
        foreach (var url in new[] { "https://google.com/", "https://google.com./", "https://a.google.com:1@google.com/", "https://x.google.com@google.com/", "https://notgoogle.com/" })
            Assert.True(Outcome(set, url) == DnrOutcome.Allow, url);
    }

    [Fact]
    public void Prefix_MatchesAtSegmentBoundaries_CaseInsensitively_AcrossPortsUserinfoAndFragments()
    {
        var set = Compile(Policy(member: [Member("Reddit.com/R/LearnProgramming", PatternType.Prefix, RuleAction.Block)]));
        foreach (var url in new[]
                 {
                     "https://www.reddit.com/r/learnprogramming", "https://reddit.com/r/learnprogramming/x", "https://reddit.com/R/LEARNPROGRAMMING?x",
                     "https://reddit.com/r/learnprogramming#f", "https://u:p@reddit.com:8443/r/learnprogramming",
                 })
            Assert.True(Outcome(set, url) == DnrOutcome.Block, url);
        foreach (var url in new[] { "https://reddit.com/r/learnprogrammingmemes", "https://reddit.com/x/r/learnprogramming", "https://reddit.com/?r=/r/learnprogramming", "https://notreddit.com/r/learnprogramming" })
            Assert.True(Outcome(set, url) == DnrOutcome.Allow, url);
        var rule = set.DynamicRules.Single(r => r.Condition.RequestDomains?.Contains("reddit.com") == true);
        Assert.False(rule.Condition.IsUrlFilterCaseSensitive);
    }

    [Fact]
    public void PrefixPaths_AreCanonicalizedLikeChromeUrls()
    {
        var set = Compile(Policy(member: [Member("example.com/a/./b/../ש d", PatternType.Prefix, RuleAction.Block)]));
        Assert.Equal(DnrOutcome.Block, Outcome(set, "https://example.com/a/ש d/x"));
        Assert.Equal(DnrOutcome.Block, Outcome(set, "https://example.com/a/%D7%A9%20d"));
        Assert.Equal(DnrOutcome.Allow, Outcome(set, "https://example.com/a/b"));
    }

    [Fact]
    public void RegexRules_AreCaseInsensitive_AndCannotBeSkippedWithUserinfoOrAFragment()
    {
        var set = Compile(Policy(member:
        [
            Member(@"^https?://([a-z0-9-]+\.)*casino\.example/$", PatternType.Regex, RuleAction.Block),
            Member(@"/Secret/", PatternType.Regex, RuleAction.Block),
        ]));
        foreach (var url in new[] { "https://casino.example/", "https://www.casino.example/", "https://u@casino.example/", "https://u:p@www.casino.example/#x", "https://a.example/Secret/x", "https://a.example/secret/x", "https://CASINO.example/" })
            Assert.True(Outcome(set, url) == DnrOutcome.Block, url);
        foreach (var url in new[] { "https://casino.example/x", "https://a.example/Secrets/x" })
            Assert.True(Outcome(set, url) == DnrOutcome.Allow, url);
        Assert.All(set.DynamicRules.Where(r => r.Condition.RequestDomains is null && r.Id >= 1000), r => Assert.False(r.Condition.IsUrlFilterCaseSensitive));
    }

    [Theory]
    [InlineData(@"(?=a)b")]
    [InlineData(@"(?<!a)b")]
    [InlineData(@"(a)\1")]
    [InlineData(@"(?>a)")]
    [InlineData(@"a++")]
    [InlineData(@"[a")]
    [InlineData(@"(")]
    [InlineData("")]
    public void RegexRulesRe2CannotRun_AreSkipped(string pattern)
    {
        var set = Compile(Policy(member: [Member(pattern, PatternType.Regex, RuleAction.Block)]));
        Assert.Equal([DnrRuleIds.YouTube, DnrRuleIds.YouTubeShort, DnrRuleIds.YouTubeSignIn, DnrRuleIds.IpLiteralGate], set.DynamicRules.Select(r => r.Id));
    }

    [Theory]
    [InlineData("example.com/x", PatternType.Domain)]
    [InlineData("exa mple.com", PatternType.Domain)]
    [InlineData("example.com", PatternType.Wildcard)]
    [InlineData("*.1.2.3.4", PatternType.Wildcard)]
    [InlineData("https://example.com/x", PatternType.Prefix)]
    [InlineData("*.example.com/x", PatternType.Prefix)]
    public void InvalidPatterns_AreSkipped(string pattern, PatternType type)
    {
        var set = Compile(Policy(member: [Member(pattern, type, RuleAction.Block)]));
        Assert.Equal(4, set.DynamicRules.Count); // the structural rules only (YouTube, youtu.be, YouTube sign-in, IP gate)
    }

    [Fact]
    public void IdnPatterns_AreEmittedInPunycode_AndAllRequestDomainsAreAsciiLowercase()
    {
        var set = Compile(Policy(member: [Member("Bücher.Example", PatternType.Domain, RuleAction.Block), Member("*.Ünï.Example", PatternType.Wildcard, RuleAction.Block)],
            categories: [new CategoryDomain("MIXED.Example.", RuleAction.Block, "c")]));
        var domains = set.DynamicRules.SelectMany(r => r.Condition.RequestDomains ?? []).ToList();
        Assert.Contains("xn--bcher-kva.example", domains);
        Assert.Contains("mixed.example", domains);
        Assert.All(domains, d => Assert.Matches("^[a-z0-9._\\-\\[\\]:]+$", d));
        Assert.Equal(DnrOutcome.Block, Outcome(set, "https://www.BÜCHER.example/"));
        Assert.Equal(DnrOutcome.Block, Outcome(set, "https://a.ünï.example/"));
        Assert.Equal(DnrOutcome.Allow, Outcome(set, "https://ünï.example/"));
    }

    [Fact]
    public void DomainsWithTheSameActionAndPriority_ArePackedIntoOneRule()
    {
        var set = Compile(Policy(member:
        [
            Member("a.example", PatternType.Domain, RuleAction.Block),
            Member("b.example", PatternType.Domain, RuleAction.Block),
            Member("c.example", PatternType.Domain, RuleAction.Block),
            Member("x.c.example", PatternType.Domain, RuleAction.Block),
            Member("*.w1.example", PatternType.Wildcard, RuleAction.Block),
            Member("*.w2.example", PatternType.Wildcard, RuleAction.Block),
        ]));
        var packed = set.DynamicRules.Single(r => r.Condition.RequestDomains?.Contains("a.example") == true);
        Assert.Equal(["a.example", "b.example", "c.example"], packed.Condition.RequestDomains);
        Assert.Single(set.DynamicRules, r => r.Condition.RequestDomains?.SequenceEqual(["w1.example", "w2.example"]) == true);
        Assert.Single(set.DynamicRules, r => r.Condition.RequestDomains?.SequenceEqual(["x.c.example"]) == true);
    }

    [Fact]
    public void MemberRules_SitAboveEveryGlobalRuleAndCategory()
    {
        var set = Compile(Policy(
            member: [Member("example.com", PatternType.Domain, RuleAction.Allow), Member(@"x", PatternType.Regex, RuleAction.Allow)],
            global: [Global("www.example.com/x/y", PatternType.Prefix, RuleAction.Block)],
            categories: [new CategoryDomain("deep.www.example.com", RuleAction.Block, "c")]));
        var memberMin = set.DynamicRules.Where(r => r.Priority >= DnrPriorities.MemberBand && r.Priority < DnrPriorities.YouTube).Min(r => r.Priority);
        var globalMax = set.DynamicRules.Where(r => r.Priority >= DnrPriorities.GlobalBand && r.Priority < DnrPriorities.MemberBand).Max(r => r.Priority);
        Assert.True(memberMin > globalMax);
        Assert.Equal(DnrOutcome.Allow, Outcome(set, "https://deep.www.example.com/x/y"));
    }

    // --- time-up session rules -----------------------------------------------------------------------------------------

    [Fact]
    public void TimeUp_BlocksEverythingButAlwaysAllowed_AboveEveryDynamicRule()
    {
        var set = Compile(Policy(FilterMode.Blocklist, YouTubeMode.Unrestricted,
            member:
            [
                Member("school.example/homework", PatternType.Prefix, RuleAction.Allow, always: true),
                Member("*.edu.example", PatternType.Wildcard, RuleAction.Allow, always: true),
                Member("fun.example", PatternType.Domain, RuleAction.Allow),
                Member("weird.example", PatternType.Domain, RuleAction.Block, always: true),
            ],
            global: [Global("mashov.info", PatternType.Domain, RuleAction.Allow, always: true), Global("192.168.1.50", PatternType.Domain, RuleAction.Allow, always: true)]));

        Assert.True(set.TimeUpSessionRules.Min(r => r.Priority) > set.DynamicRules.Max(r => r.Priority));
        var block = set.TimeUpSessionRules.Single(r => r.Id == DnrRuleIds.TimeUpBlock);
        Assert.Equal(DnrPriorities.TimeUpBlock, block.Priority);
        Assert.All(set.TimeUpSessionRules.Where(r => r != block), r => Assert.Equal("allow", r.Action.Type));

        foreach (var url in new[] { "https://mashov.info/login", "https://www.mashov.info/", "https://school.example/homework/1", "https://a.edu.example/", "http://192.168.1.50/" })
            Assert.True(TimeUpOutcome(set, url) == DnrOutcome.Allow, url);
        foreach (var url in new[] { "https://fun.example/", "https://school.example/games", "https://edu.example/", "https://www.youtube.com/", "https://other.example/", "https://weird.example/" })
            Assert.True(TimeUpOutcome(set, url) == DnrOutcome.Block, url);
    }

    // --- JSON shape ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Json_HasTheExactShapeTheExtensionPassesToChrome()
    {
        var set = Compile(Policy(FilterMode.Allowlist, YouTubeMode.Off,
            member: [Member("*.w.example", PatternType.Wildcard, RuleAction.Allow), Member("p.example/x", PatternType.Prefix, RuleAction.Block), Member("re", PatternType.Regex, RuleAction.Allow)],
            global: [Global("mashov.info", PatternType.Domain, RuleAction.Allow, always: true)]));
        var json = JsonSerializer.Serialize(set);

        Assert.DoesNotContain("null", json);
        Assert.Contains($"\"regexSubstitution\":\"chrome-extension://{ExtensionId}/blocked.html#\\\\0\"", json);

        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(["DynamicRules", "TimeUpSessionRules"], root.Select(p => p.Key));
        foreach (var rule in root["DynamicRules"]!.AsArray().Concat(root["TimeUpSessionRules"]!.AsArray()).Select(r => r!.AsObject()))
        {
            Assert.Equal(["id", "priority", "action", "condition"], rule.Select(p => p.Key));
            Assert.True(rule["id"]!.GetValue<int>() >= 1 && rule["priority"]!.GetValue<int>() >= 1);
            var action = rule["action"]!.AsObject();
            switch (action["type"]!.GetValue<string>())
            {
                case "allow":
                    Assert.Equal(["type"], action.Select(p => p.Key));
                    break;
                case "redirect":
                    Assert.Equal(["type", "redirect"], action.Select(p => p.Key));
                    Assert.Equal(["regexSubstitution"], action["redirect"]!.AsObject().Select(p => p.Key));
                    Assert.NotNull(rule["condition"]!["regexFilter"]);
                    break;
                default:
                    Assert.Fail($"unexpected action {action}");
                    break;
            }
            var condition = rule["condition"]!.AsObject();
            Assert.Equal(["main_frame", "sub_frame"], condition["resourceTypes"]!.AsArray().Select(t => t!.GetValue<string>()));
            Assert.All(condition.Select(p => p.Key), k => Assert.Contains(k, new[] { "resourceTypes", "requestDomains", "regexFilter", "isUrlFilterCaseSensitive" }));
        }

        var roundTrip = JsonSerializer.Deserialize<DnrRuleSet>(json)!;
        Assert.Equal(json, JsonSerializer.Serialize(roundTrip));
    }

    // --- determinism and stability -------------------------------------------------------------------------------------

    private static readonly Rule[] MixedMemberRules =
    [
        Member("a.example", PatternType.Domain, RuleAction.Block),
        Member("*.b.example", PatternType.Wildcard, RuleAction.Allow),
        Member("c.example/x/", PatternType.Prefix, RuleAction.Block),
        Member("c.example/x/y", PatternType.Prefix, RuleAction.Allow),
        Member(@"^https?://d\.example/", PatternType.Regex, RuleAction.Block),
        Member("e.example", PatternType.Domain, RuleAction.Allow, always: true),
    ];

    private static readonly CategoryDomain[] Categories =
        Enumerable.Range(0, 500).Select(i => new CategoryDomain($"cat{i:D4}.example", RuleAction.Block, "c")).ToArray();

    [Fact]
    public void Compilation_IsDeterministic_RegardlessOfInputOrder()
    {
        var first = JsonSerializer.Serialize(Compile(Policy(member: MixedMemberRules, categories: Categories)));
        var again = JsonSerializer.Serialize(Compile(Policy(member: MixedMemberRules, categories: Categories)));
        var shuffled = JsonSerializer.Serialize(Compile(Policy(member: Enumerable.Reverse(MixedMemberRules), categories: Enumerable.Reverse(Categories))));
        Assert.Equal(first, again);
        Assert.Equal(first, shuffled);
    }

    [Fact]
    public void RuleIds_AreUnique_AndRulesAreOrderedByPriority()
    {
        var set = Compile(Policy(FilterMode.Allowlist, member: MixedMemberRules, categories: Categories));
        var ids = set.DynamicRules.Concat(set.TimeUpSessionRules).Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        foreach (var rules in new[] { set.DynamicRules, set.TimeUpSessionRules })
        {
            var ordered = rules.OrderByDescending(r => r.Priority).ThenBy(r => r.Id).ToList();
            Assert.Equal(ordered, rules);
        }
    }

    [Fact]
    public void UnchangedRules_KeepTheirIdAndContent_WhenOtherRulesChange()
    {
        var before = Compile(Policy(member: MixedMemberRules, categories: Categories));
        var after = Compile(Policy(
            member: MixedMemberRules.Append(Member("new.example/z", PatternType.Prefix, RuleAction.Allow)).Append(Member("zz.example", PatternType.Domain, RuleAction.Allow)),
            global: [Global("g.example", PatternType.Domain, RuleAction.Allow)],
            categories: Categories));

        var unchanged = before.DynamicRules.Select(r => JsonSerializer.Serialize(r)).Intersect(after.DynamicRules.Select(r => JsonSerializer.Serialize(r))).Count();
        Assert.Equal(before.DynamicRules.Count - 1, unchanged); // only the member domain-allow rule gained a domain
        var categoryRule = before.DynamicRules.Single(r => r.Condition.RequestDomains?.Contains("cat0000.example") == true);
        Assert.Contains(after.DynamicRules, r => JsonSerializer.Serialize(r) == JsonSerializer.Serialize(categoryRule));
    }

    // --- scale and limits ----------------------------------------------------------------------------------------------

    [Fact]
    public void A100kDomainCategory_CompilesIntoAFewRules_WithinTheLimits()
    {
        var domains = Enumerable.Range(0, 100_000)
            .Select(i => new CategoryDomain(i % 10 == 0 ? $"www.d{i:D6}.example" : $"d{i:D6}.example", RuleAction.Block, "big"))
            .ToList();
        var set = Compile(Policy(FilterMode.Blocklist, categories: domains));

        Assert.True(set.DynamicRules.Count <= 7, $"{set.DynamicRules.Count} rules");
        Assert.True(set.DynamicRules.Count(r => r.Action.Type == "redirect") <= DnrLimits.MaxUnsafeDynamicRules);
        Assert.Equal(100_000, set.DynamicRules.Sum(r => r.Condition.RequestDomains?.Count(d => d.StartsWith('d') || d.StartsWith("www.")) ?? 0));
        Assert.Equal(DnrOutcome.Block, Outcome(set, "https://d054321.example/"));
        Assert.Equal(DnrOutcome.Block, Outcome(set, "https://m.www.d000010.example/"));
        Assert.Equal(DnrOutcome.Allow, Outcome(set, "https://d000010.example/"));
        Assert.Equal(DnrOutcome.Allow, Outcome(set, "https://elsewhere.example/"));

        var allow = Compile(Policy(FilterMode.Allowlist, categories: domains.Select(d => d with { Action = RuleAction.Allow })));
        Assert.True(allow.DynamicRules.Count <= 7);
        Assert.Equal(DnrOutcome.Allow, Outcome(allow, "https://sub.d099999.example/"));
        Assert.Equal(DnrOutcome.Block, Outcome(allow, "https://elsewhere.example/"));
    }

    [Fact]
    public void TooManyRegexRules_Throw()
    {
        var rules = Enumerable.Range(0, DnrLimits.MaxRegexRules).Select(i => Member($"^https://r{i}\\.example/", PatternType.Regex, RuleAction.Block));
        var ex = Assert.Throws<DnrCompileException>(() => Compile(Policy(member: rules)));
        Assert.Contains("regex", ex.Message);
        Assert.Contains("1,000", ex.Message);
    }

    [Fact]
    public void ALongPrefixPath_BlockWidensToADirectoryThatFits_AllowIsDropped()
    {
        var fits = "/" + new string('a', DnrLimits.MaxPrefixPathLength - 1);
        var exact = Compile(Policy(member: [Member("example.com" + fits, PatternType.Prefix, RuleAction.Allow)]));
        Assert.Equal(DnrOutcome.Allow, Outcome(exact, "https://example.com" + fits));

        var longPath = "/dir/" + new string('b', DnrLimits.MaxPrefixPathLength) + "/page";
        var block = Compile(Policy(member: [Member("example.com" + longPath, PatternType.Prefix, RuleAction.Block)]));
        Assert.Equal(DnrOutcome.Block, Outcome(block, "https://example.com" + longPath));
        Assert.Equal(DnrOutcome.Block, Outcome(block, "https://example.com/dir/other"));
        Assert.Equal(DnrOutcome.Allow, Outcome(block, "https://example.com/elsewhere"));

        var allow = Compile(Policy(FilterMode.Allowlist, member: [Member("example.com" + longPath, PatternType.Prefix, RuleAction.Allow)]));
        Assert.Equal(DnrOutcome.Block, Outcome(allow, "https://example.com" + longPath));
    }

    [Fact]
    public void AWildcardWithTooManyLabels_BlockWidensToTheBaseDomain_AllowIsDropped()
    {
        var fits = Compile(Policy(member: [Member("*.a.b.c.d.e.f.g.h", PatternType.Wildcard, RuleAction.Block)]));
        Assert.Equal(DnrOutcome.Block, Outcome(fits, "https://x.a.b.c.d.e.f.g.h/"));
        Assert.Equal(DnrOutcome.Allow, Outcome(fits, "https://a.b.c.d.e.f.g.h/"));

        var block = Compile(Policy(member: [Member("*.a.b.c.d.e.f.g.h.i", PatternType.Wildcard, RuleAction.Block)]));
        Assert.Equal(DnrOutcome.Block, Outcome(block, "https://x.a.b.c.d.e.f.g.h.i/"));
        Assert.Equal(DnrOutcome.Block, Outcome(block, "https://a.b.c.d.e.f.g.h.i/"));

        var allow = Compile(Policy(FilterMode.Allowlist, member: [Member("*.a.b.c.d.e.f.g.h.i", PatternType.Wildcard, RuleAction.Allow)]));
        Assert.Equal(DnrOutcome.Block, Outcome(allow, "https://x.a.b.c.d.e.f.g.h.i/"));
    }

    [Fact]
    public void FitPrefix_KeepsShortPaths_AndCutsLongOnesAtADirectory()
    {
        Assert.Equal("example.com/a/b", DnrPatterns.FitPrefix("example.com", "/a/b"));
        var fitted = DnrPatterns.FitPrefix("example.com", "/dir/" + new string('ש', 60) + "/x");
        Assert.Equal("example.com/dir/", fitted);
        Compile(Policy(member: [Member(fitted, PatternType.Prefix, RuleAction.Allow)]));
    }
}

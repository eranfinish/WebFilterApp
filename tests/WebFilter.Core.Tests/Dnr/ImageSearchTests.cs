using System.Text.Json.Nodes;
using WebFilter.Core.Decisions;
using WebFilter.Core.Dnr;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;
using WebFilter.Core.Urls;
using static WebFilter.Core.Tests.Dnr.DnrTestPolicies;

namespace WebFilter.Core.Tests.Dnr;

/// <summary>
/// PROTECTION-SPEC §4: the built-in image-search blocks of a member with blockImageSearch. The Core evaluator and the
/// compiled DNR rules (through <see cref="DnrSimulator"/>) must agree on every URL, including the time-up session rules,
/// and the blocks must win over the member's own allow and always-allowed rules.
/// </summary>
public class ImageSearchTests
{
    public static readonly string[] ImageSearchUrls =
    [
        "https://www.google.com/search?q=cats&udm=2",
        "https://www.google.com/search?udm=2&q=cats",
        "https://www.google.com/search?q=cats&sca_esv=1&udm=2&fbs=AB",
        "https://www.google.com/search?q=cats&tbm=isch",
        "https://www.google.com/search?tbm=isch&q=cats&hl=he",
        "https://www.google.co.il/search?q=%D7%97%D7%AA%D7%95%D7%9C&udm=2&sa=X",
        "https://google.de/search?q=x&source=lnms&tbm=isch&sa=X#imgrc=abc",
        "https://www.google.com/search?q=cats&udm=2#frag",
        "https://WWW.GOOGLE.COM/search?q=cats&UDM=2",
        "https://www.google.com./search?q=cats&udm=2",
        "https://user:pw@www.google.com/search?q=x&udm=2",
        "http://www.google.com:443/search?q=x&tbm=isch",
        "https://www.google.com/imghp?hl=en",
        "https://images.google.com/",
        "https://images.google.co.il/imghp?hl=iw",
        "https://lens.google.com/uploadbyurl?url=https://x.example/a.png",
        "https://www.bing.com/images/search?q=cats",
        "https://bing.com/images",
        "https://www.bing.com/images?q=cats",
        "https://duckduckgo.com/?q=cats&iax=images&ia=images",
        "https://duckduckgo.com/?ia=images&q=cats",
        "https://yandex.ru/images/search?text=cats",
        "https://yandex.com/images/",
        "https://www.yandex.com.tr/images?text=x",
    ];

    public static readonly string[] OtherUrls =
    [
        "https://www.google.com/search?q=cats",
        "https://www.google.com/search?q=cats&udm=14",
        "https://www.google.com/search?q=cats&udm=21",
        "https://www.google.com/search?q=udm%3D2",
        "https://www.google.com/search?q=tbm=isch",
        "https://www.google.com/search?q=cats#udm=2",
        "https://www.google.com/search?q=cats&tbm=nws",
        "https://www.google.com/search?q=cats&tbm=ischx",
        "https://www.google.com/maps/search/cats?udm=2",
        "https://mail.google.com/mail/u/0/#search/udm=2",
        "https://news.google.com/search?q=x&udm=2",
        "https://www.google.com.evil.example/search?udm=2",
        "https://notgoogle.com/search?q=x&udm=2",
        "https://www.bing.com/search?q=cats",
        "https://www.bing.com/imagesfoo",
        "https://duckduckgo.com/?q=cats&ia=web",
        "https://duckduckgo.com/?q=cats&ia=imagesx",
        "https://yandex.ru/search/?text=cats",
        "https://images.example.com/",
        "https://www.youtube.com/results?search_query=cats",
    ];

    private static EffectiveUrlPolicy WithImageSearch(EffectiveUrlPolicy policy) => policy with { BuiltinBlocks = BuiltinRules.ImageSearch };

    public static TheoryData<string> Images() => [.. ImageSearchUrls];

    public static TheoryData<string> Others() => [.. OtherUrls];

    [Theory]
    [MemberData(nameof(Images))]
    public void Image_search_pages_are_blocked_by_the_evaluator_and_the_compiled_rules(string url)
    {
        var policy = WithImageSearch(Policy(FilterMode.Blocklist));
        var rules = DnrCompiler.Compile(policy, Options);

        Assert.True(UrlNormalizer.TryNormalize(url, out var normalized), url);
        var core = RuleEvaluator.Evaluate(policy, normalized);
        Assert.Equal((Decision.Block, ReasonCode.BlockedImageSearch), (core.Decision, core.Reason));
        Assert.StartsWith(BuiltinRules.ImageSearchIdPrefix, core.RuleId);
        Assert.Equal(DnrOutcome.Block, DnrSimulator.Evaluate(rules.DynamicRules, url));
        Assert.Equal(DnrOutcome.Block, ReferenceEvaluator.Evaluate(policy, ChromeUrl.Parse(url)));

        // The redirect carries the original URL to the block page, like every other block.
        var winner = DnrSimulator.Match(rules.DynamicRules, ChromeUrl.Parse(url))!;
        Assert.Equal(DnrPriorities.BuiltinBlock, winner.Priority);
        Assert.Equal($"chrome-extension://{ExtensionId}/blocked.html#{ChromeUrl.Parse(url).Spec}", DnrSimulator.RedirectTarget(winner, ChromeUrl.Parse(url)));
    }

    [Theory]
    [MemberData(nameof(Others))]
    public void Web_search_and_other_pages_are_not_image_search(string url)
    {
        var policy = WithImageSearch(Policy(FilterMode.Blocklist));
        Assert.True(UrlNormalizer.TryNormalize(url, out var normalized), url);
        Assert.Null(RuleEvaluator.BuiltinBlock(policy, normalized));
        Assert.Equal(DnrOutcome.Allow, DnrSimulator.Evaluate(DnrCompiler.Compile(policy, Options).DynamicRules, url));
    }

    [Fact]
    public void Without_the_flag_or_for_an_unrestricted_member_nothing_is_added()
    {
        // A teen in blocklist mode, so that the ai-chat category (not chosen) adds nothing either.
        var teen = new FamilyMember("m", "M", Role.Teen, 14, FilterMode.Blocklist, YouTubeMode.Off, BlockImageSearch: false);
        var config = new FamilyConfig { TimeZone = TimeZoneInfo.Utc, Members = [teen] };
        Assert.Empty(BuiltinRules.For(config, teen));
        Assert.Equal(BuiltinRules.ImageSearch, BuiltinRules.For(config, teen with { BlockImageSearch = true }));
        Assert.Empty(BuiltinRules.For(config, teen with { BlockImageSearch = true, FilterMode = FilterMode.Unrestricted }));

        var plain = Policy(FilterMode.Blocklist);
        Assert.Equal(DnrOutcome.Allow, DnrSimulator.Evaluate(DnrCompiler.Compile(plain, Options).DynamicRules, ImageSearchUrls[0]));
        Assert.Empty(DnrCompiler.Compile(WithImageSearch(Policy(FilterMode.Unrestricted)), Options).DynamicRules);
    }

    [Fact]
    public void Ids_are_stable_and_named_builtin_image_search()
    {
        Assert.All(BuiltinRules.ImageSearch, b =>
        {
            Assert.StartsWith("builtin-image-search-", b.Rule.Id);
            Assert.Equal(ReasonCode.BlockedImageSearch, b.Reason);
            Assert.Equal(RuleAction.Block, b.Rule.Action);
            Assert.Null(RuleValidator.Validate(b.Rule));
        });
        Assert.Equal(BuiltinRules.ImageSearch.Count, BuiltinRules.ImageSearch.Select(b => b.Rule.Id).Distinct().Count());
        Assert.Equal("blocked_image_search", ReasonCode.BlockedImageSearch.ToWire());
    }

    /// <summary>A parent's allow rules, always-allowed sites and time-up do not reopen image search: only the flag does.</summary>
    [Fact]
    public void Allow_and_always_allowed_rules_and_time_up_do_not_reopen_image_search()
    {
        var policy = WithImageSearch(Policy(FilterMode.Allowlist,
            member: [Member("google.com", PatternType.Domain, RuleAction.Allow, always: true), Member("www.bing.com/images", PatternType.Prefix, RuleAction.Allow)],
            global: [Global("duckduckgo.com", PatternType.Domain, RuleAction.Allow, always: true), Global(@"^https?://yandex\.ru/", PatternType.Regex, RuleAction.Allow)]));
        var rules = DnrCompiler.Compile(policy, Options);
        foreach (var url in ImageSearchUrls)
        {
            Assert.True(UrlNormalizer.TryNormalize(url, out var normalized), url);
            Assert.Equal(ReasonCode.BlockedImageSearch, RuleEvaluator.Evaluate(policy, normalized).Reason);
            Assert.Equal(DnrOutcome.Block, Outcome(rules, url));
            Assert.Equal(DnrOutcome.Block, TimeUpOutcome(rules, url));
        }
        Assert.Equal(DnrOutcome.Allow, Outcome(rules, "https://www.google.com/search?q=cats"));
        Assert.Equal(DnrOutcome.Allow, TimeUpOutcome(rules, "https://www.google.com/search?q=cats"));
        Assert.Equal(DnrOutcome.Allow, TimeUpOutcome(rules, "https://duckduckgo.com/?q=cats"));
    }

    /// <summary>The service's decision in every state, for a child whose google.com is always allowed (school sign-in).</summary>
    [Fact]
    public void The_engine_blocks_image_search_in_every_state_the_member_is_filtered_in()
    {
        var config = Fixture(blockImageSearch: true);
        config.Rules.Add(new Rule("r-google", "noa", "google.com", PatternType.Domain, RuleAction.Allow, AlwaysAllowed: true));
        var hub = new HubCore(config);
        var inWindow = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00", System.Globalization.CultureInfo.InvariantCulture);
        var morning = DateTimeOffset.Parse("2026-11-02T10:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture); // outside the window
        const string image = "https://www.google.com/search?q=cats&udm=2", web = "https://www.google.com/search?q=cats";

        DecisionResult Decide(string url, DateTimeOffset now) => Decider.Url(hub.View(), "noa", url, now);

        Assert.Equal(ReasonCode.BlockedImageSearch, Decide(image, inWindow).Reason);
        Assert.Equal(ReasonCode.AllowedAlways, Decide(web, inWindow).Reason);
        Assert.Equal(ReasonCode.BlockedImageSearch, Decide(image, morning).Reason);
        Assert.Equal(ReasonCode.AllowedAlways, Decide(web, morning).Reason);

        hub.StartHomework("noa", 30, inWindow);
        Assert.Equal(ReasonCode.BlockedImageSearch, Decide(image, inWindow.AddMinutes(1)).Reason);
        hub.StopHomework("noa", inWindow);
        hub.Pause("noa", inWindow.AddHours(1), inWindow);
        Assert.Equal(ReasonCode.AllowedPaused, Decide(image, inWindow.AddMinutes(1)).Reason);

        // yoav (no flag) is not affected.
        Assert.NotEqual(ReasonCode.BlockedImageSearch, Decider.Url(hub.View(), "yoav", image, inWindow).Reason);
    }

    /// <summary>
    /// Random policies over the search engines' hosts, with and without the flag: the Core evaluator and the compiled
    /// rules agree on every URL, and the time-up session rules agree with the oracle (built-in blocks first, then
    /// always-allowed).
    /// </summary>
    [Fact]
    public void Random_policies_with_image_search_agree_between_the_evaluator_and_the_compiled_rules()
    {
        string[] hosts = ["google.com", "www.google.com", "google.co.il", "images.google.com", "lens.google.com", "bing.com", "www.bing.com", "duckduckgo.com", "yandex.ru", "example.com"];
        string[] paths = ["", "/", "/search", "/images", "/imghp", "/images/search"];
        // Unanchored user regexes that could match inside a fragment are left out: Chrome's URL keeps the fragment and the
        // evaluator's does not (a known gap of user regexes, DnrPatterns.FromRegex), unrelated to the built-in blocks.
        string[] regexes = [@"^https?://(www\.)?google\.com/search", @"/search\?", "images", @"^https?://duckduckgo\.com/\?"];
        var random = new Random(20261022);
        var urls = ImageSearchUrls.Concat(OtherUrls).Concat(SignInAndAiModeTests.AiModeUrls).Concat(SignInAndAiModeTests.NotAiModeUrls)
            .Where(u => !u.Contains("youtube", StringComparison.Ordinal)).ToList();
        var failures = new List<string>();

        for (var iteration = 0; iteration < 300 && failures.Count < 10; iteration++)
        {
            Rule RandomRule(bool member)
            {
                var action = random.Next(3) == 0 ? RuleAction.Block : RuleAction.Allow;
                var always = action == RuleAction.Allow && random.Next(3) == 0;
                var host = hosts[random.Next(hosts.Length)];
                var (pattern, type) = random.Next(4) switch
                {
                    0 => (host, PatternType.Domain),
                    1 => ("*." + host, PatternType.Wildcard),
                    2 => (host + paths[random.Next(paths.Length)], PatternType.Prefix),
                    _ => (regexes[random.Next(regexes.Length)], PatternType.Regex),
                };
                return member ? Member(pattern, type, action, always) : Global(pattern, type, action, always);
            }

            var policy = Policy(random.Next(2) == 0 ? FilterMode.Allowlist : FilterMode.Blocklist, YouTubeMode.Restricted,
                Enumerable.Range(0, random.Next(5)).Select(_ => RandomRule(true)), Enumerable.Range(0, random.Next(5)).Select(_ => RandomRule(false)));
            // None, image search, AI Mode (PROTECTION-SPEC §11), or both.
            var builtins = random.Next(4);
            var flagged = (builtins & 1) != 0;
            var aiMode = (builtins & 2) != 0;
            policy = policy with { BuiltinBlocks = [.. flagged ? BuiltinRules.ImageSearch : [], .. aiMode ? BuiltinRules.AiMode : []] };
            var rules = DnrCompiler.Compile(policy, Options);
            var hasRegex = policy.MemberRules.Concat(policy.GlobalRules).Any(r => r.Type == PatternType.Regex);

            foreach (var raw in urls)
            {
                var chrome = ChromeUrl.Parse(raw);
                // Known gap (user regexes only): Chrome keeps a trailing host dot in the URL a regexFilter sees.
                if (hasRegex && chrome.Host.EndsWith('.')) continue;
                Assert.True(UrlNormalizer.TryNormalize(raw, out var normalized), raw);
                var core = RuleEvaluator.Evaluate(policy, normalized).Decision == Decision.Allow ? DnrOutcome.Allow : DnrOutcome.Block;
                var dnr = DnrSimulator.Evaluate(rules.DynamicRules, chrome);
                if (core != dnr) failures.Add($"#{iteration} {raw}: core {core}, DNR {dnr} (flag {flagged})");
                if (flagged && ImageSearchUrls.Contains(raw) && core != DnrOutcome.Block) failures.Add($"#{iteration} {raw}: image search allowed");
                if (aiMode && SignInAndAiModeTests.AiModeUrls.Contains(raw) && core != DnrOutcome.Block) failures.Add($"#{iteration} {raw}: AI Mode allowed");

                var timeUp = DnrSimulator.Evaluate(rules.TimeUpSessionRules.Concat(rules.DynamicRules), chrome);
                if (timeUp != ReferenceEvaluator.EvaluateTimeUp(policy, chrome)) failures.Add($"#{iteration} time-up {raw}: DNR {timeUp}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static FamilyConfig Fixture(bool blockImageSearch)
    {
        var config = FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);
        var index = config.Members.FindIndex(m => m.Id == "noa");
        config.Members[index] = config.Members[index] with { BlockImageSearch = blockImageSearch };
        return config;
    }
}

using System.Text.Json.Nodes;
using WebFilter.Core.Decisions;
using WebFilter.Core.Dnr;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;
using WebFilter.Core.Urls;
using WebFilter.Core.YouTube;
using static WebFilter.Core.Tests.Dnr.DnrTestPolicies;

namespace WebFilter.Core.Tests.Dnr;

/// <summary>
/// PROTECTION-SPEC §11, each checked in the Core decision and in the compiled DNR rules (through <see cref="DnrSimulator"/>):
/// Google's AI Mode is blocked with the ai-chat category, YouTube off does not break Google sign-in, and the school
/// sign-in prefix rules the admin app adds with school-tools work for a child in allowlist mode.
/// </summary>
public class SignInAndAiModeTests
{
    private static readonly DateTimeOffset InWindow = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Morning = DateTimeOffset.Parse("2026-11-02T10:00:00+02:00", System.Globalization.CultureInfo.InvariantCulture);

    public static readonly string[] AiModeUrls =
    [
        "https://www.google.com/search?q=homework+help&udm=50",
        "https://www.google.com/search?udm=50&q=x",
        "https://www.google.co.il/search?q=x&aep=1&udm=50&sa=X#frag",
        "https://google.com/search?udm=50",
        "https://user:pw@www.google.com/search?q=x&udm=50",
        "https://www.google.com./search?q=x&udm=50",
    ];

    public static readonly string[] NotAiModeUrls =
    [
        "https://www.google.com/search?q=homework+help",
        "https://www.google.com/search?q=x&udm=500",
        "https://www.google.com/search?q=x&udm=5",
        "https://www.google.com/search?q=udm%3D50",
        "https://www.google.com/search?q=udm=50",
        "https://www.google.com/search?q=x#udm=50",
        "https://news.google.com/search?q=x&udm=50",
        "https://www.google.com/maps/search/x?udm=50",
    ];

    public static TheoryData<string> AiMode() => [.. AiModeUrls];

    public static TheoryData<string> NotAiMode() => [.. NotAiModeUrls];

    // ---- AI Mode ------------------------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AiMode))]
    public void AI_Mode_is_blocked_by_the_evaluator_and_the_compiled_rules(string url)
    {
        var policy = Policy(FilterMode.Blocklist, member: [Member("google.com", PatternType.Domain, RuleAction.Allow, always: true)]) with { BuiltinBlocks = BuiltinRules.AiMode };
        var rules = DnrCompiler.Compile(policy, Options);

        Assert.True(UrlNormalizer.TryNormalize(url, out var normalized), url);
        Assert.Equal(DecisionResult.Block(ReasonCode.BlockedRule, "builtin-ai-mode-google"), RuleEvaluator.Evaluate(policy, normalized));
        Assert.Equal(DnrOutcome.Block, Outcome(rules, url));
        Assert.Equal(DnrOutcome.Block, TimeUpOutcome(rules, url));
        Assert.Equal(DnrOutcome.Block, ReferenceEvaluator.Evaluate(policy, ChromeUrl.Parse(url)));
    }

    [Theory]
    [MemberData(nameof(NotAiMode))]
    public void Plain_search_is_not_AI_Mode(string url)
    {
        var policy = Policy(FilterMode.Blocklist) with { BuiltinBlocks = BuiltinRules.AiMode };
        Assert.True(UrlNormalizer.TryNormalize(url, out var normalized), url);
        Assert.Null(RuleEvaluator.BuiltinBlock(policy, normalized));
        Assert.Equal(DnrOutcome.Allow, Outcome(DnrCompiler.Compile(policy, Options), url));
    }

    [Fact]
    public void The_ai_chat_category_blocks_AI_Mode_when_blocked_for_the_member_or_not_allowed_in_allowlist_mode()
    {
        var config = Fixture();
        var noa = config.Member("noa");   // child, allowlist
        var yoav = config.Member("yoav"); // teen, blocklist
        Assert.True(BuiltinRules.BlocksCategory(config, noa, BuiltinRules.AiChatCategory));
        Assert.False(BuiltinRules.BlocksCategory(config, yoav, BuiltinRules.AiChatCategory));
        Assert.Equal(BuiltinRules.AiMode, BuiltinRules.For(config, noa));
        Assert.Empty(BuiltinRules.For(config, yoav));

        config.MemberCategories.Add(new MemberCategory("yoav", "ai-chat", RuleAction.Block));
        config.MemberCategories.Add(new MemberCategory("noa", "ai-chat", RuleAction.Allow));
        Assert.True(BuiltinRules.BlocksCategory(config, yoav, BuiltinRules.AiChatCategory));
        Assert.False(BuiltinRules.BlocksCategory(config, noa, BuiltinRules.AiChatCategory));
        Assert.Equal([.. BuiltinRules.ImageSearch, .. BuiltinRules.AiMode], BuiltinRules.For(config, yoav with { BlockImageSearch = true }));
        Assert.False(BuiltinRules.BlocksCategory(config, config.Member("savta"), BuiltinRules.AiChatCategory)); // unrestricted
        Assert.True(BuiltinRules.IsBuiltinBlock("builtin-ai-mode-google"));
        Assert.False(BuiltinRules.IsBuiltinBlock("builtin-school-signin-a"));
    }

    /// <summary>The engine (every state) and the compiled rules agree for a teen with ai-chat blocked and Google always allowed.</summary>
    [Theory]
    [InlineData("normal")]
    [InlineData("homework")]
    [InlineData("time_up")]
    [InlineData("paused")]
    public void AI_Mode_blocks_agree_with_the_service_decision_in_every_state(string state)
    {
        var config = Fixture();
        config.MemberCategories.Add(new MemberCategory("yoav", "ai-chat", RuleAction.Block));
        config.Rules.Add(new Rule("r-google", "yoav", "google.com", PatternType.Domain, RuleAction.Allow, AlwaysAllowed: true));
        var hub = new HubCore(config);
        var now = state == "time_up" ? Morning : InWindow;
        if (state == "homework") hub.StartHomework("yoav", 60, now);
        if (state == "paused") hub.Pause("yoav", now.AddHours(1), now);
        var account = hub.Config.Device("pc-kids").Accounts.Single(a => a.MemberId == "yoav");
        var view = hub.View();
        var effective = ExtensionPolicy.For(view, account, now);
        var compiled = effective.UrlPolicy is null ? new DnrRuleSet([], []) : DnrCompiler.Compile(effective.UrlPolicy, Options);
        IEnumerable<DnrRule> installed = state == "time_up" ? compiled.TimeUpSessionRules.Concat(compiled.DynamicRules) : compiled.DynamicRules;

        foreach (var url in AiModeUrls.Concat(NotAiModeUrls))
        {
            var decision = Decider.Url(view, "yoav", url, now);
            var service = decision.Decision == Decision.Allow ? DnrOutcome.Allow : DnrOutcome.Block;
            Assert.True(service == DnrSimulator.Evaluate(installed, url), $"{state} {url}: service {decision}");
            if (state != "paused" && AiModeUrls.Contains(url)) Assert.Equal(ReasonCode.BlockedRule, decision.Reason);
        }
    }

    // ---- YouTube off and Google sign-in ------------------------------------------------------------------------------

    [Theory]
    [InlineData("accounts.youtube.com", true)]
    [InlineData("ACCOUNTS.youtube.com", true)]
    [InlineData("accounts.youtube.com.", true)]
    [InlineData("www.youtube.com", false)]
    [InlineData("youtube.com", false)]
    [InlineData("accounts.youtube.com.evil.example", false)]
    [InlineData("myaccounts.youtube.com", false)]
    public void Only_accounts_youtube_com_is_the_sign_in_host(string host, bool signIn)
    {
        Assert.Equal(signIn, YouTubeUrlParser.IsSignInHost(host));
        Assert.Equal("accounts.youtube.com", YouTubeUrlParser.SignInHost);
    }

    /// <summary>
    /// accounts.youtube.com stays allowed in every YouTube mode, Off included, in normal, homework and unlinked states,
    /// while the other YouTube hosts follow the mode; the engine and the compiled rules agree everywhere (time up blocks it
    /// like everything else that is not always allowed).
    /// </summary>
    [Theory]
    [InlineData("off", "normal")]
    [InlineData("off", "homework")]
    [InlineData("off", "time_up")]
    [InlineData("allowlist", "normal")]
    [InlineData("restricted", "homework")]
    [InlineData("off", "unlinked")]
    public void YouTube_off_does_not_block_Google_sign_in(string mode, string state)
    {
        var config = Fixture();
        foreach (var id in new[] { "noa", "yoav" })
        {
            var i = config.Members.FindIndex(m => m.Id == id);
            config.Members[i] = config.Members[i] with { YouTubeMode = FixtureLoader.ParseYouTubeMode(mode) };
        }
        var hub = new HubCore(config);
        var now = state == "time_up" ? Morning : InWindow;
        if (state == "homework") { hub.StartHomework("noa", 60, now); hub.StartHomework("yoav", 60, now); }
        var view = hub.View();
        var accounts = hub.Config.Device("pc-kids").Accounts.Where(a => state == "unlinked" ? a.Name == "visitor" : a.MemberId is "noa" or "yoav");
        const string signIn = "https://accounts.youtube.com/accounts/SetSID?ssdc=1&sidt=x";

        foreach (var account in accounts)
        {
            var effective = ExtensionPolicy.For(view, account, now);
            var compiled = DnrCompiler.Compile(effective.UrlPolicy!, Options);
            IEnumerable<DnrRule> installed = state == "time_up" ? compiled.TimeUpSessionRules.Concat(compiled.DynamicRules) : compiled.DynamicRules;
            foreach (var url in new[] { signIn, "https://www.youtube.com/", "https://m.youtube.com/watch?v=abc", "https://studio.youtube.com/" })
            {
                var decision = account.MemberId is null ? Decider.UnlinkedUrl(view, url, now) : Decider.Url(view, account.MemberId, url, now);
                var service = decision.Decision == Decision.Allow ? DnrOutcome.Allow : DnrOutcome.Block;
                Assert.True(service == DnrSimulator.Evaluate(installed, url), $"{mode} {state} {account.Name} {url}: service {decision}");
                if (url == signIn) Assert.Equal(state == "time_up" ? Decision.Block : Decision.Allow, decision.Decision);
                else if (mode == "off" || state is "homework" or "unlinked" or "time_up") Assert.Equal(Decision.Block, decision.Decision);
            }
        }
    }

    // ---- School sign-in for children in allowlist mode ---------------------------------------------------------------

    /// <summary>
    /// The Ministry of Education's Google sign-in goes through www.google.com/a/&lt;domain&gt;/…: when a parent picks
    /// school-tools, the admin app adds global prefix allows www.google.com/a/ and www.google.com/recaptcha/. For a child in
    /// allowlist mode those pages open while Google search stays blocked, in the engine and the compiled rules alike.
    /// </summary>
    [Fact]
    public void School_sign_in_prefix_rules_open_the_sign_in_pages_but_not_Google_search_for_a_child_in_allowlist_mode()
    {
        var config = Fixture();
        config.Categories["school-tools"] = ["accounts.google.com", "classroom.google.com", "docs.google.com", "drive.google.com", "meet.google.com"];
        config.MemberCategories.Add(new MemberCategory("noa", "school-tools", RuleAction.Allow));
        config.Rules.Add(new Rule("builtin-school-signin-a", null, "www.google.com/a/", PatternType.Prefix, RuleAction.Allow));
        config.Rules.Add(new Rule("builtin-school-signin-recaptcha", null, "www.google.com/recaptcha/", PatternType.Prefix, RuleAction.Allow));
        Assert.All(config.Rules, r => Assert.Null(RuleValidator.Validate(r)));
        var hub = new HubCore(config);
        var view = hub.View();
        var account = hub.Config.Device("pc-kids").Accounts.Single(a => a.MemberId == "noa");
        var rules = DnrCompiler.Compile(ExtensionPolicy.For(view, account, InWindow).UrlPolicy!, Options);

        (string Url, Decision Expected, ReasonCode Reason)[] cases =
        [
            ("https://www.google.com/a/example.k12.il/ServiceLogin?service=classroom&passive=true&continue=https://classroom.google.com/", Decision.Allow, ReasonCode.AllowedRule),
            ("https://www.google.com/a/example.k12.il/acs", Decision.Allow, ReasonCode.AllowedRule),
            ("https://www.google.com/recaptcha/api2/anchor?k=x", Decision.Allow, ReasonCode.AllowedRule),
            ("https://accounts.google.com/ServiceLogin?continue=x", Decision.Allow, ReasonCode.AllowedCategory),
            ("https://classroom.google.com/u/0/h", Decision.Allow, ReasonCode.AllowedCategory),
            ("https://www.google.com/search?q=x", Decision.Block, ReasonCode.BlockedNotInAllowlist),
            ("https://www.google.com/", Decision.Block, ReasonCode.BlockedNotInAllowlist),
            ("https://www.google.com/about", Decision.Block, ReasonCode.BlockedNotInAllowlist),
            ("https://www.google.com/abc/ServiceLogin", Decision.Block, ReasonCode.BlockedNotInAllowlist),
        ];
        foreach (var (url, expected, reason) in cases)
        {
            var decision = Decider.Url(view, "noa", url, InWindow);
            Assert.True((expected, reason) == (decision.Decision, decision.Reason), $"{url}: {decision}");
            Assert.Equal(expected == Decision.Allow ? DnrOutcome.Allow : DnrOutcome.Block, Outcome(rules, url));
        }
    }

    private static FamilyConfig Fixture() =>
        FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);
}

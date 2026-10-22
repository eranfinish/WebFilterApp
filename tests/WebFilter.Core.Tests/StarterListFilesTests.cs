using System.Text.Json;
using WebFilter.Core.Dnr;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Dnr;
using WebFilter.Core.Urls;

namespace WebFilter.Core.Tests;

/// <summary>
/// data/starter-lists (docs/PROTECTION-SPEC.md §3): every file is a usable category, and the protection lists compile
/// within the DNR limits and decide as intended, in the DNR rules and in the Core evaluator alike.
/// </summary>
public sealed class StarterListFilesTests
{
    private static readonly string ListsDir = Path.Combine(FindRepoRoot(), "data", "starter-lists");

    /// <summary>Allowed for children (allowlist mode) and, in the teen policy below, alongside the blocking lists.</summary>
    private static readonly string[] AllowLists = ["educational", "school-tools"];

    /// <summary>Every list a teen policy may block (§3), including the optional ai-chat.</summary>
    private static readonly string[] BlockLists = ["adult", "gambling", "proxies", "ai-chat", "unfiltered-search", "ads-popups"];

    /// <summary>The lists of §3: written sorted and de-duplicated, by hand or by tools/starter-lists/update.mjs.</summary>
    private static readonly string[] ProtectionLists = ["school-tools", .. BlockLists];

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> Lists = new(Load);

    private static readonly Lazy<EffectiveUrlPolicy> Teen = new(() => Policy(FilterMode.Blocklist, BlockLists));
    private static readonly Lazy<EffectiveUrlPolicy> Child = new(() => Policy(FilterMode.Allowlist, []));
    private static readonly Lazy<DnrRuleSet> TeenRules = new(() => DnrCompiler.Compile(Teen.Value, DnrTestPolicies.Options));
    private static readonly Lazy<DnrRuleSet> ChildRules = new(() => DnrCompiler.Compile(Child.Value, DnrTestPolicies.Options));

    // --- the files -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_file_is_json_with_its_name_as_category_and_a_domain_list()
    {
        var files = Directory.GetFiles(ListsDir, "*.json");
        Assert.True(files.Length >= 10, $"{files.Length} files in {ListsDir}");
        foreach (var file in files)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            var root = json.RootElement;
            Assert.True(root.TryGetProperty("category", out var category) && category.ValueKind == JsonValueKind.String, $"{file}: no category");
            Assert.Equal(Path.GetFileNameWithoutExtension(file), category.GetString());
            Assert.True(root.TryGetProperty("domains", out var domains) && domains.ValueKind == JsonValueKind.Array, $"{file}: no domains");
            Assert.True(domains.GetArrayLength() >= 10, $"{file}: {domains.GetArrayLength()} domains");
            Assert.All(domains.EnumerateArray(), d => Assert.Equal(JsonValueKind.String, d.ValueKind));
        }
        Assert.All(ProtectionLists, name => Assert.True(Lists.Value.ContainsKey(name), $"{name}.json is missing"));
    }

    [Fact]
    public void Every_domain_is_a_normalized_domain_name()
    {
        foreach (var (name, domains) in Lists.Value)
        {
            foreach (var domain in domains)
            {
                Assert.True(UrlNormalizer.TryNormalizeHost(domain, out var normalized, out var kind), $"{name}: '{domain}' is not a host");
                Assert.True(normalized == domain, $"{name}: '{domain}' normalizes to '{normalized}'");
                Assert.True(kind == HostKind.Domain && domain.Contains('.'), $"{name}: '{domain}' is not a domain name");
            }
        }
    }

    [Fact]
    public void No_domain_appears_twice_in_one_list_or_in_two_lists()
    {
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, domains) in Lists.Value)
        {
            foreach (var domain in domains)
                Assert.True(owner.TryAdd(domain, name), $"'{domain}' is in {owner.GetValueOrDefault(domain)} and {name}");
        }
    }

    [Fact]
    public void Protection_lists_are_sorted_and_leave_out_subdomains_of_listed_domains()
    {
        foreach (var name in ProtectionLists)
        {
            var domains = Lists.Value[name];
            Assert.Equal(domains.Order(StringComparer.Ordinal), domains);
            var set = domains.ToHashSet(StringComparer.Ordinal);
            var covered = domains.Where(d => Parents(d).Any(set.Contains)).ToList();
            Assert.True(covered.Count == 0, $"{name}: the parent domain is already listed for {string.Join(", ", covered.Take(10))}");
        }
    }

    // --- compiled policies ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_teen_policy_with_every_blocking_list_compiles_into_a_few_rules_within_the_limits()
    {
        var policy = Teen.Value;
        var set = TeenRules.Value;

        Assert.True(set.DynamicRules.Count <= 30, $"{set.DynamicRules.Count} dynamic rules");
        Assert.True(set.DynamicRules.Count <= DnrLimits.MaxDynamicRules);
        Assert.True(set.DynamicRules.Count(r => r.Action.Type == "redirect") <= DnrLimits.MaxUnsafeDynamicRules);
        Assert.True(set.TimeUpSessionRules.Count <= DnrLimits.MaxSessionRules);
        Assert.True(set.DynamicRules.Concat(set.TimeUpSessionRules).Count(r => r.Condition.RegexFilter is not null) <= DnrLimits.MaxRegexRules);

        // Every category domain is in exactly one rule's requestDomains (the YouTube rules are fixed, not categories).
        var listed = set.DynamicRules
            .Where(r => r.Id is not (DnrRuleIds.YouTube or DnrRuleIds.YouTubeShort))
            .Sum(r => r.Condition.RequestDomains?.Count ?? 0);
        Assert.Equal(policy.CategoryDomains.Count, listed);
        Assert.True(listed <= 150_000, $"{listed} domains in the teen policy");
    }

    [Fact]
    public void A_child_policy_allowing_educational_and_school_tools_compiles_within_the_limits()
    {
        var set = ChildRules.Value;
        Assert.True(set.DynamicRules.Count <= 20, $"{set.DynamicRules.Count} dynamic rules");
        Assert.True(set.DynamicRules.Count(r => r.Action.Type == "redirect") <= DnrLimits.MaxUnsafeDynamicRules);
    }

    // --- spot checks ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Proxies_list_translate_goog_and_school_tools_list_classroom()
    {
        Assert.Contains("translate.goog", Lists.Value["proxies"]);
        Assert.Contains("classroom.google.com", Lists.Value["school-tools"]);
        // Precise Google and Microsoft hosts: allowing google.com or microsoft.com would allow search, Gemini and Copilot too.
        Assert.DoesNotContain("google.com", Lists.Value["school-tools"]);
        Assert.DoesNotContain("microsoft.com", Lists.Value["school-tools"]);
    }

    [Theory]
    [InlineData("https://www.google.com/search?q=homework")]
    [InlineData("https://google.com/search?q=homework")]
    [InlineData("https://www.google.co.il/search?q=%D7%A9%D7%99%D7%A2%D7%95%D7%A8%D7%99%D7%9D")]
    public void No_list_blocks_a_plain_google_search(string url)
    {
        var host = new Uri(url).Host;
        foreach (var name in BlockLists)
        {
            var hit = Lists.Value[name].FirstOrDefault(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
            Assert.True(hit is null, $"{name} blocks {url} with '{hit}'");
        }
        AssertTeen(url, allowed: true);
    }

    [Theory]
    [InlineData("https://classroom.google.com/c/MTIzNDU2Nzg5")]
    [InlineData("https://accounts.google.com/v3/signin/identifier?continue=https%3A%2F%2Fclassroom.google.com")]
    [InlineData("https://docs.google.com/document/d/abc/edit")]
    [InlineData("https://drive.google.com/drive/my-drive")]
    [InlineData("https://meet.google.com/abc-defg-hij")]
    [InlineData("https://login.microsoftonline.com/common/oauth2/v2.0/authorize")]
    [InlineData("https://login.live.com/login.srf")]
    [InlineData("https://teams.microsoft.com/v2/")]
    [InlineData("https://m365.cloud.microsoft/apps/")]
    [InlineData("https://outlook.office.com/mail/")]
    [InlineData("https://tau-my.sharepoint.com/personal/x")]
    [InlineData("https://us02web.zoom.us/j/123456789")]
    [InlineData("https://lgn.edu.gov.il/nidp/")]
    [InlineData("https://web.mashov.info/students/login")]
    public void School_sites_are_allowed_for_children_and_teens(string url)
    {
        AssertChild(url, allowed: true);
        AssertTeen(url, allowed: true);
    }

    [Theory]
    [InlineData("https://www.google.com/search?q=anything")]
    [InlineData("https://gemini.google.com/app")]
    [InlineData("https://mail.google.com/mail/u/0/")]
    [InlineData("https://www.microsoft.com/he-il/")]
    [InlineData("https://copilot.microsoft.com/")]
    public void Other_google_and_microsoft_sites_stay_blocked_for_children(string url) => AssertChild(url, allowed: false);

    [Theory]
    [InlineData("https://www.bing.com/search?q=homework")]
    [InlineData("https://translate.google.com/?sl=en&tl=iw&op=translate")]
    [InlineData("https://he.wikipedia.org/wiki/%D7%99%D7%A9%D7%A8%D7%90%D7%9C")]
    [InlineData("https://www.ynet.co.il/news")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://github.com/")]
    [InlineData("https://www.amazon.com/")]
    public void Ordinary_sites_stay_allowed_for_teens(string url) => AssertTeen(url, allowed: true);

    [Theory]
    [InlineData("https://www-example-com.translate.goog/?_x_tr_sl=auto&_x_tr_tl=iw")]
    [InlineData("https://www.croxyproxy.com/")]
    [InlineData("https://archive.ph/abcde")]
    [InlineData("https://www.pornhub.com/")]
    [InlineData("https://xhamster.com/")]
    [InlineData("https://www.bet365.com/")]
    [InlineData("https://www.winner.co.il/")]
    [InlineData("https://chatgpt.com/")]
    [InlineData("https://gemini.google.com/app")]
    [InlineData("https://copilot.cloud.microsoft/")]
    [InlineData("https://duckduckgo.com/?q=x")]
    [InlineData("https://yandex.ru/search/?text=x")]
    [InlineData("https://www.googleadservices.com/pagead/aclk?sa=L&ai=x")]
    [InlineData("https://ad.doubleclick.net/ddm/trackclk/N123.456/B789")]
    public void Teens_are_blocked_from_the_blocking_lists(string url) => AssertTeen(url, allowed: false);

    [Fact]
    public void Display_ad_frames_are_blocked_for_teens()
    {
        Assert.Equal(DnrOutcome.Block, DnrSimulator.Evaluate(TeenRules.Value.DynamicRules, "https://googleads.g.doubleclick.net/pagead/ads?client=ca-pub-1", "sub_frame"));
        Assert.Equal(DnrOutcome.Block, DnrSimulator.Evaluate(TeenRules.Value.DynamicRules, "https://tpc.googlesyndication.com/safeframe/1-0-40/html/container.html", "sub_frame"));
    }

    // --- helpers -------------------------------------------------------------------------------------------------------

    private static void AssertTeen(string url, bool allowed) => AssertBoth(Teen.Value, TeenRules.Value, url, allowed);

    private static void AssertChild(string url, bool allowed) => AssertBoth(Child.Value, ChildRules.Value, url, allowed);

    /// <summary>The compiled DNR rules and the Core evaluator reach the same, expected decision.</summary>
    private static void AssertBoth(EffectiveUrlPolicy policy, DnrRuleSet rules, string url, bool allowed)
    {
        var dnr = DnrSimulator.Evaluate(rules.DynamicRules, url);
        Assert.True(dnr == (allowed ? DnrOutcome.Allow : DnrOutcome.Block), $"{policy.MemberId}: DNR {dnr} for {url}");
        Assert.True(UrlNormalizer.TryNormalize(url, out var normalized), url);
        var decision = RuleEvaluator.Evaluate(policy, normalized);
        Assert.True(decision.IsAllowed == allowed, $"{policy.MemberId}: evaluator {decision} for {url}");
    }

    /// <summary>The allow lists allowed, the given lists blocked, YouTube restricted (its hosts follow the YouTube mode).</summary>
    private static EffectiveUrlPolicy Policy(FilterMode mode, IEnumerable<string> blockLists)
    {
        var categories = AllowLists.SelectMany(name => Lists.Value[name].Select(d => new CategoryDomain(d, RuleAction.Allow, name)))
            .Concat(blockLists.SelectMany(name => Lists.Value[name].Select(d => new CategoryDomain(d, RuleAction.Block, name))))
            .ToList();
        return new EffectiveUrlPolicy(mode == FilterMode.Allowlist ? "child" : "teen", mode, YouTubeMode.Restricted, [], [], categories);
    }

    private static IEnumerable<string> Parents(string domain)
    {
        for (var dot = domain.IndexOf('.'); dot >= 0 && domain.IndexOf('.', dot + 1) >= 0; dot = domain.IndexOf('.', dot + 1))
            yield return domain[(dot + 1)..];
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Load()
    {
        var lists = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(ListsDir, "*.json"))
        {
            using var json = JsonDocument.Parse(File.ReadAllText(file));
            lists[Path.GetFileNameWithoutExtension(file)] =
                [.. json.RootElement.GetProperty("domains").EnumerateArray().Select(d => d.GetString()!)];
        }
        return lists;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("WebFilter.slnx not found above " + AppContext.BaseDirectory);
    }
}

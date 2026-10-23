using System.Globalization;
using System.Text;
using WebFilter.Core.Model;

namespace WebFilter.Core.Dnr;

/// <summary>
/// Compiles a member's <see cref="EffectiveUrlPolicy"/> into declarativeNetRequest rules whose outcome (allow vs block)
/// equals <see cref="Urls.RuleEvaluator"/> for every URL, plus the YouTube-mode and IP-literal rules (not for the parents'
/// baseline, <see cref="EffectiveUrlPolicy.Baseline"/>, which has only its category blocks and the parents' allow rules).
/// Only main_frame and sub_frame are filtered. Blocks redirect to the block page with the original URL.
/// See spikes/S3-S4/REPORT.md for what DNR supports and its limits, and <see cref="DnrPriorities"/> for the layout.
/// </summary>
public static class DnrCompiler
{
    internal static readonly IReadOnlyList<string> Frames = ["main_frame", "sub_frame"];

    /// <summary>Hosts whose access the YouTube mode alone decides (with their subdomains).</summary>
    internal static readonly IReadOnlyList<string> YouTubeDomains = ["youtube-nocookie.com", "youtube.com"];

    /// <summary>youtu.be itself (not its subdomains).</summary>
    internal const string YouTubeShortHost = "youtu.be";

    /// <exception cref="DnrCompileException">The policy does not fit within <see cref="DnrLimits"/>.</exception>
    public static DnrRuleSet Compile(EffectiveUrlPolicy policy, DnrCompileOptions options)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ExtensionId) || options.ExtensionId.IndexOfAny(['/', '#', '\\', ':']) >= 0)
            throw new ArgumentException("ExtensionId must be a bare extension id.", nameof(options));
        if (!options.BlockPagePath.StartsWith('/') || options.BlockPagePath.Contains('#'))
            throw new ArgumentException("BlockPagePath must start with '/' and contain no '#'.", nameof(options));

        if (policy.Mode == FilterMode.Unrestricted)
            return new DnrRuleSet([], []);

        var redirect = new DnrAction("redirect", new DnrRedirect(RegexSubstitution: $"chrome-extension://{options.ExtensionId}{options.BlockPagePath}#\\0"));
        var dynamicRules = CompileDynamic(policy, redirect);
        // Time never applies to the parents' baseline (PROTECTION-SPEC §13): no time-up rules.
        var sessionRules = policy.Baseline ? [] : CompileTimeUp(policy, redirect);
        CheckLimits(policy.MemberId, dynamicRules, sessionRules);
        return new DnrRuleSet(dynamicRules, sessionRules);
    }

    private static List<DnrRule> CompileDynamic(EffectiveUrlPolicy policy, DnrAction redirect)
    {
        var builder = new RuleSetBuilder("dynamic", redirect);

        // The parents' baseline (PROTECTION-SPEC §13) blocks its category only: no default, no IP-literal or YouTube rules.
        if (policy.Mode == FilterMode.Allowlist && !policy.Baseline)
            builder.AddFixed(DnrRuleIds.AllowlistDefault, DnrPriorities.AllowlistDefault, block: true, null, DnrPatterns.AnyWebUrl, null);
        else if (!policy.Baseline)
            // Below every rule: an IP-literal URL that no rule matches is blocked. When rules match, the winner decides —
            // a winning allow rule is exactly the "explicit allow" that lifts the IP block (README §5), and a block stays a block.
            builder.AddFixed(DnrRuleIds.IpLiteralGate, DnrPriorities.IpLiteralGate, block: true, null, DnrPatterns.IpLiteralHost, null);

        foreach (var rule in policy.GlobalRules)
            builder.AddRule(rule, DnrPriorities.GlobalBand);
        foreach (var category in policy.CategoryDomains)
        {
            // Categories never lift the IP-literal block (only explicit rules do), so an IP literal in an allow category is ignored.
            if (category.Action == RuleAction.Allow && DnrPatterns.IsIpLiteral(category.Domain))
                continue;
            if (DnrPatterns.FromDomain(category.Domain) is { } match)
                builder.Add(match, DnrPriorities.GlobalBand, category.Action == RuleAction.Block);
        }
        foreach (var rule in policy.MemberRules)
            builder.AddRule(rule, DnrPriorities.MemberBand);

        // Built-in blocks (image search) sit above everything, the time-up session rules included, as in the evaluator.
        foreach (var builtin in policy.BuiltinBlocks)
        {
            if (DnrPatterns.FromRule(builtin.Rule) is { } match)
                builder.AddAtPriority(match, DnrPriorities.BuiltinBlock, block: true);
        }

        if (policy.Baseline)
            return builder.Build();

        // YouTube hosts follow the YouTube mode only: off → block, otherwise allow (the content script decides channels).
        var youTubeBlock = policy.YouTube == YouTubeMode.Off;
        builder.AddFixed(DnrRuleIds.YouTube, DnrPriorities.YouTube, youTubeBlock, YouTubeDomains,
            youTubeBlock ? DnrPatterns.WholeUrl : null, null);
        var exact = DnrPatterns.ExactHostOnly(2);
        builder.AddFixed(DnrRuleIds.YouTubeShort, DnrPriorities.YouTube, youTubeBlock, [YouTubeShortHost], exact, null);
        // Google sign-in's YouTube cookie host: allowed in every YouTube mode (at equal priority allow beats the block).
        builder.AddFixed(DnrRuleIds.YouTubeSignIn, DnrPriorities.YouTube, block: false, [YouTube.YouTubeUrlParser.SignInHost], null, null);

        return builder.Build();
    }

    /// <summary>While time is up: block everything except always-allowed rules, above every dynamic rule.</summary>
    private static List<DnrRule> CompileTimeUp(EffectiveUrlPolicy policy, DnrAction redirect)
    {
        var builder = new RuleSetBuilder("session", redirect);
        builder.AddFixed(DnrRuleIds.TimeUpBlock, DnrPriorities.TimeUpBlock, block: true, null, DnrPatterns.AnyWebUrl, null);
        foreach (var rule in policy.AlwaysAllowedRules.Where(r => r.Action == RuleAction.Allow))
        {
            if (DnrPatterns.FromRule(rule) is { } match)
                builder.AddAtPriority(match, DnrPriorities.TimeUpAlwaysAllowed, block: false);
        }
        return builder.Build();
    }

    private static void CheckLimits(string memberId, List<DnrRule> dynamicRules, List<DnrRule> sessionRules)
    {
        static int Unsafe(List<DnrRule> rules) => rules.Count(r => r.Action.Type == "redirect");
        static int Regex(List<DnrRule> rules) => rules.Count(r => r.Condition.RegexFilter is not null);

        void Check(int count, int limit, string what)
        {
            if (count > limit)
                throw new DnrCompileException(
                    string.Create(CultureInfo.InvariantCulture, $"Member '{memberId}': {count:N0} {what} exceed the declarativeNetRequest limit of {limit:N0}."));
        }

        Check(dynamicRules.Count, DnrLimits.MaxDynamicRules, "dynamic rules");
        Check(Unsafe(dynamicRules), DnrLimits.MaxUnsafeDynamicRules, "dynamic redirect (unsafe) rules");
        Check(sessionRules.Count, DnrLimits.MaxSessionRules, "session rules");
        Check(Unsafe(sessionRules), DnrLimits.MaxUnsafeSessionRules, "session redirect (unsafe) rules");
        Check(Regex(dynamicRules) + Regex(sessionRules), DnrLimits.MaxRegexRules, "regex rules (dynamic and session combined)");
    }

    /// <summary>Groups conditions with the same priority, action and regex into one rule with a merged requestDomains list.</summary>
    private sealed class RuleSetBuilder(string set, DnrAction redirect)
    {
        private static readonly DnrAction Allow = new("allow");
        private readonly Dictionary<string, Group> _groups = new(StringComparer.Ordinal);
        private readonly List<DnrRule> _fixed = [];

        public void AddFixed(int id, int priority, bool block, IReadOnlyList<string>? domains, string? regex, bool? caseSensitive) =>
            _fixed.Add(new DnrRule(id, priority, block ? redirect : Allow,
                new DnrCondition(Frames, RequestDomains: domains, RegexFilter: regex, IsUrlFilterCaseSensitive: caseSensitive)));

        public void AddRule(Rule rule, int band)
        {
            if (DnrPatterns.FromRule(rule) is { } match)
                Add(match, band, rule.Action == RuleAction.Block);
        }

        public void Add(DnrMatch match, int band, bool block) =>
            AddAtPriority(match, DnrPriorities.InBand(band, match.Level, block), block);

        public void AddAtPriority(DnrMatch match, int priority, bool block)
        {
            var regex = block ? match.BlockRegex : match.AllowRegex;
            var domainless = match.Domain is null;
            var key = $"{set}|{priority}|{(block ? "block" : "allow")}|{(domainless ? "any" : "domains")}|{match.CaseSensitive?.ToString() ?? "-"}|{regex}";
            if (!_groups.TryGetValue(key, out var group))
                _groups[key] = group = new Group(key, priority, block, regex, match.CaseSensitive, domainless);
            if (match.Domain is not null)
                group.Domains.Add(match.Domain);
        }

        public List<DnrRule> Build()
        {
            var used = new HashSet<int>(_fixed.Select(r => r.Id));
            var rules = new List<DnrRule>(_fixed);
            foreach (var group in _groups.Values.OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var id = StableId(group.Key);
                while (!used.Add(id))
                    id = id + 1 >= DnrRuleIds.FirstHashed + DnrRuleIds.HashedRange ? DnrRuleIds.FirstHashed : id + 1;
                var condition = new DnrCondition(Frames,
                    RequestDomains: group.Domainless ? null : group.Domains.ToList(),
                    RegexFilter: group.Regex,
                    IsUrlFilterCaseSensitive: group.CaseSensitive);
                rules.Add(new DnrRule(id, group.Priority, group.Block ? redirect : Allow, condition));
            }
            rules.Sort((a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Id.CompareTo(b.Id));
            return rules;
        }

        // FNV-1a over the group key: the same logical rule keeps its id when other rules change.
        private static int StableId(string key)
        {
            var hash = 14695981039346656037UL;
            foreach (var b in Encoding.UTF8.GetBytes(key))
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            return DnrRuleIds.FirstHashed + (int)(hash % DnrRuleIds.HashedRange);
        }

        private sealed record Group(string Key, int Priority, bool Block, string? Regex, bool? CaseSensitive, bool Domainless)
        {
            public SortedSet<string> Domains { get; } = new(StringComparer.Ordinal);
        }
    }
}

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using WebFilter.Core.Decisions;
using WebFilter.Core.Model;

namespace WebFilter.Core.Urls;

/// <summary>
/// Steps 5–8 of the decision order in MVP-PLAN §6 for a restricted member, on an already-normalized web URL.
/// Time, pause, homework and YouTube are decided by the engine before this is called.
/// </summary>
/// <remarks>
/// A policy is compiled once into a host-suffix index and cached per <see cref="EffectiveUrlPolicy"/> instance, so a
/// policy must be treated as an immutable snapshot: build a new one when rules change. Invalid patterns never match
/// (the admin UI validates them with <see cref="PatternMatcher.TryCompile"/>).
/// </remarks>
public static class RuleEvaluator
{
    private static readonly ConditionalWeakTable<EffectiveUrlPolicy, PolicyIndex> Indexes = new();
    private static readonly ConditionalWeakTable<Rule, CompiledRule> CompiledRules = new();
    private static readonly ConditionalWeakTable<IReadOnlyList<string>, CategorySet> CategorySets = new();

    /// <summary>
    /// <list type="number">
    /// <item>IP literal → <c>blocked_ip_literal</c>, unless an allow rule (member or global) matches that IP.</item>
    /// <item>Member rules: if any match, the most specific wins (tie → block).
    /// allow → <c>allowed_rule</c> (or <c>allowed_always</c> if the rule is always-allowed); block → <c>blocked_rule</c>.</item>
    /// <item>Global rules and the member's category domains together: most specific wins (tie → block).
    /// Category domains behave like domain rules; reasons <c>allowed_category</c> / <c>blocked_category</c>.</item>
    /// <item>Default: allowlist → <c>blocked_not_in_allowlist</c>; blocklist → <c>allowed_default</c>.</item>
    /// </list>
    /// RuleId in the result is the winning rule's id (null for categories and defaults).
    /// </summary>
    /// <remarks>
    /// An IP literal that an allow rule matches continues through steps 2–4, so a more specific (or member) block rule
    /// still wins. Among equally specific matches with the same action, a rule beats a category domain and an
    /// always-allowed rule beats a plain one; after that the first in policy order wins.
    /// </remarks>
    public static DecisionResult Evaluate(EffectiveUrlPolicy policy, NormalizedUrl url)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(url);
        if (policy.Mode == FilterMode.Unrestricted)
            return DecisionResult.Allow(ReasonCode.AllowedUnrestricted);

        var index = GetIndex(policy);
        string? href = null;
        var member = index.Member.FindBest(url, ref href);
        MatchResult? global = null;

        if (url.IsIpLiteral && !member.AnyAllow)
        {
            global = index.Global.FindBest(url, ref href);
            if (!global.Value.AnyAllow)
                return DecisionResult.Block(ReasonCode.BlockedIpLiteral);
        }

        if (member.Best is { } memberWinner)
            return ToResult(memberWinner);

        global ??= index.Global.FindBest(url, ref href);
        if (global.Value.Best is { } globalWinner)
            return ToResult(globalWinner);

        return policy.Mode == FilterMode.Allowlist
            ? DecisionResult.Block(ReasonCode.BlockedNotInAllowlist)
            : DecisionResult.Allow(ReasonCode.AllowedDefault);
    }

    /// <summary>True if an always-allowed rule (member or global) matches the URL; returns its id.</summary>
    /// <remarks>Only allow rules count. With several matches the most specific one's id is returned.</remarks>
    public static bool MatchesAlwaysAllowed(EffectiveUrlPolicy policy, NormalizedUrl url, out string? ruleId)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(url);
        string? href = null;
        var match = GetIndex(policy).AlwaysAllowed.FindBest(url, ref href);
        ruleId = match.Best?.Rule?.Id;
        return match.Best is not null;
    }

    /// <summary>True if the URL's host is in the given category for this policy's category domains (used by homework mode).</summary>
    /// <remarks>Category lists are cached per list instance (revalidated by count); replace a list rather than editing it in place.</remarks>
    public static bool IsInCategory(IReadOnlyDictionary<string, IReadOnlyList<string>> categories, string category, NormalizedUrl url)
    {
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(url);
        if (!categories.TryGetValue(category, out var domains))
            return false;

        var set = CategorySets.GetValue(domains, static list => new CategorySet(list));
        if (set.SourceCount != domains.Count)
        {
            set = new CategorySet(domains);
            CategorySets.AddOrUpdate(domains, set);
        }
        return set.Contains(url);
    }

    private static PolicyIndex GetIndex(EffectiveUrlPolicy policy) => Indexes.GetValue(policy, static p => new PolicyIndex(p));

    private static CompiledPattern? CompileRule(Rule rule) =>
        CompiledRules.GetValue(rule, static r =>
            new CompiledRule(PatternMatcher.TryCompile(r.Pattern, r.Type, out var compiled, out _) ? compiled : null)).Pattern;

    private static DecisionResult ToResult(Entry winner)
    {
        if (winner.Rule is null)
        {
            return winner.IsAllow
                ? DecisionResult.Allow(ReasonCode.AllowedCategory)
                : DecisionResult.Block(ReasonCode.BlockedCategory);
        }
        if (!winner.IsAllow)
            return DecisionResult.Block(ReasonCode.BlockedRule, winner.Rule.Id);
        return DecisionResult.Allow(winner.IsAlwaysAllowed ? ReasonCode.AllowedAlways : ReasonCode.AllowedRule, winner.Rule.Id);
    }

    private sealed record CompiledRule(CompiledPattern? Pattern);

    /// <summary>A rule or category domain with its compiled pattern. <see cref="Rule"/> is null for category domains.</summary>
    private sealed class Entry(CompiledPattern pattern, Rule? rule, bool isAllow, int order)
    {
        public CompiledPattern Pattern { get; } = pattern;
        public Rule? Rule { get; } = rule;
        public bool IsAllow { get; } = isAllow;
        public bool IsAlwaysAllowed { get; } = isAllow && rule?.AlwaysAllowed == true;
        public int Order { get; } = order;

        /// <summary>Tie → block; then rule over category; then always-allowed; then policy order.</summary>
        public bool BeatsOnTie(Entry other) =>
            IsAllow != other.IsAllow ? !IsAllow
            : (Rule is null) != (other.Rule is null) ? Rule is not null
            : IsAlwaysAllowed != other.IsAlwaysAllowed ? IsAlwaysAllowed
            : Order < other.Order;
    }

    private readonly record struct MatchResult(Entry? Best, bool AnyAllow);

    private sealed class PolicyIndex
    {
        public RuleSet Member { get; } = new();
        public RuleSet Global { get; } = new();
        public RuleSet AlwaysAllowed { get; } = new();

        public PolicyIndex(EffectiveUrlPolicy policy)
        {
            var order = 0;
            foreach (var rule in policy.MemberRules)
                AddRule(Member, rule, order++);
            foreach (var rule in policy.GlobalRules)
                AddRule(Global, rule, order++);
            foreach (var category in policy.CategoryDomains)
            {
                if (PatternMatcher.TryCompile(category.Domain, PatternType.Domain, out var compiled, out _))
                    Global.Add(new Entry(compiled, null, category.Action == RuleAction.Allow, order++));
            }
            Member.Freeze();
            Global.Freeze();
            AlwaysAllowed.Freeze();
        }

        private void AddRule(RuleSet set, Rule rule, int order)
        {
            if (CompileRule(rule) is not { } compiled)
                return;
            var entry = new Entry(compiled, rule, rule.Action == RuleAction.Allow, order);
            set.Add(entry);
            if (entry.IsAlwaysAllowed)
                AlwaysAllowed.Add(entry);
        }
    }

    /// <summary>
    /// Rules indexed by pattern host. A URL host is looked up once per suffix ("a.b.com", "b.com", "com"),
    /// so the cost depends on the host's label count, not on the number of rules. Regex rules are scanned in chunks
    /// behind a combined-pattern prefilter.
    /// </summary>
    private sealed class RuleSet
    {
        private const int RegexChunkSize = 32;

        private readonly Dictionary<string, List<Entry>> _byHost = new(StringComparer.Ordinal);
        private readonly List<(RegexPattern Pattern, Entry Entry)> _regexes = [];
        private readonly List<RegexChunk> _regexChunks = [];
        private Dictionary<string, List<Entry>>.AlternateLookup<ReadOnlySpan<char>> _lookup;

        public void Add(Entry entry)
        {
            switch (entry.Pattern)
            {
                case HostPattern hostPattern:
                    if (!_byHost.TryGetValue(hostPattern.Host, out var list))
                        _byHost[hostPattern.Host] = list = [];
                    list.Add(entry);
                    break;
                case RegexPattern regex:
                    _regexes.Add((regex, entry));
                    break;
            }
        }

        public void Freeze()
        {
            _lookup = _byHost.GetAlternateLookup<ReadOnlySpan<char>>();
            for (var start = 0; start < _regexes.Count; start += RegexChunkSize)
            {
                var chunk = _regexes.GetRange(start, Math.Min(RegexChunkSize, _regexes.Count - start));
                _regexChunks.Add(new RegexChunk(chunk.Count > 1 ? PatternMatcher.TryCombineRegexes(chunk.Select(r => r.Pattern)) : null, chunk));
            }
        }

        public MatchResult FindBest(NormalizedUrl url, ref string? href)
        {
            Entry? best = null;
            var bestSpecificity = default(Specificity);
            var anyAllow = false;

            void Consider(Entry entry, Specificity specificity)
            {
                if (entry.IsAllow && entry.Rule is not null)
                    anyAllow = true;
                var comparison = best is null ? 1 : specificity.CompareTo(bestSpecificity);
                if (comparison > 0 || (comparison == 0 && entry.BeatsOnTie(best!)))
                {
                    best = entry;
                    bestSpecificity = specificity;
                }
            }

            if (_byHost.Count > 0)
            {
                var suffix = url.Host.AsSpan();
                while (true)
                {
                    if (_lookup.TryGetValue(suffix, out var entries))
                    {
                        foreach (var entry in entries)
                        {
                            if (entry.Pattern.Matches(url, out var specificity))
                                Consider(entry, specificity);
                        }
                    }
                    // IP literals only match patterns for the same address.
                    var dot = url.HostKind == HostKind.Domain ? suffix.IndexOf('.') : -1;
                    if (dot < 0)
                        break;
                    suffix = suffix[(dot + 1)..];
                }
            }

            if (_regexChunks.Count > 0)
            {
                href ??= url.Href;
                foreach (var chunk in _regexChunks)
                {
                    // Most URLs match no regex rule: one pass over the combined pattern rules the whole chunk out.
                    if (chunk.Combined is { } combined && !combined.IsMatch(href))
                        continue;
                    foreach (var (pattern, entry) in chunk.Members)
                    {
                        if (pattern.IsMatch(href))
                            Consider(entry, new Specificity(Specificity.RegexTier, 0, 0));
                    }
                }
            }

            return new MatchResult(best, anyAllow);
        }

        private sealed record RegexChunk(Regex? Combined, List<(RegexPattern Pattern, Entry Entry)> Members);
    }

    /// <summary>A category's domains, normalized once, for suffix lookups.</summary>
    private sealed class CategorySet
    {
        private readonly HashSet<string> _hosts = new(StringComparer.Ordinal);
        private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _lookup;

        public CategorySet(IReadOnlyList<string> domains)
        {
            SourceCount = domains.Count;
            foreach (var domain in domains)
            {
                if (UrlNormalizer.TryNormalizeHost(domain, out var host, out _))
                    _hosts.Add(host);
            }
            _lookup = _hosts.GetAlternateLookup<ReadOnlySpan<char>>();
        }

        public int SourceCount { get; }

        public bool Contains(NormalizedUrl url)
        {
            if (url.IsIpLiteral)
                return _hosts.Contains(url.Host);

            var suffix = url.Host.AsSpan();
            while (true)
            {
                if (_lookup.Contains(suffix))
                    return true;
                var dot = suffix.IndexOf('.');
                if (dot < 0)
                    return false;
                suffix = suffix[(dot + 1)..];
            }
        }
    }
}

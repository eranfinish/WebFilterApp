namespace WebFilter.Core.Model;

/// <summary>A category domain as it applies to one member.</summary>
public sealed record CategoryDomain(string Domain, RuleAction Action, string Category);

/// <summary>
/// Everything the URL rule evaluator and the DNR compiler need for one member at one instant:
/// active member rules, active global rules, the member's category domains, and the built-in blocks of the member's
/// protection flags (<see cref="BuiltinBlocks"/>). Expired rules are excluded. The parents' baseline protection is a
/// policy of its own (<see cref="ForBaseline"/>).
/// </summary>
public sealed record EffectiveUrlPolicy(
    string MemberId,
    FilterMode Mode,
    YouTubeMode YouTube,
    IReadOnlyList<Rule> MemberRules,
    IReadOnlyList<Rule> GlobalRules,
    IReadOnlyList<CategoryDomain> CategoryDomains)
{
    /// <summary>Blocks that win over every rule above (<see cref="BuiltinRules"/>), e.g. image search.</summary>
    public IReadOnlyList<BuiltinBlock> BuiltinBlocks { get; init; } = [];

    /// <summary>
    /// The parents' baseline protection (PROTECTION-SPEC §13, built by <see cref="ForBaseline"/>): a blocklist without the
    /// IP-literal block and without YouTube or time-up rules, whose category blocks report <c>blocked_baseline_adult</c>.
    /// </summary>
    public bool Baseline { get; init; }

    public static EffectiveUrlPolicy For(FamilyConfig config, string memberId, DateTimeOffset now)
    {
        var member = config.Member(memberId);
        var memberRules = config.Rules.Where(r => r.MemberId == memberId && r.IsActiveAt(now)).ToList();
        var globalRules = config.Rules.Where(r => r.MemberId is null && r.IsActiveAt(now)).ToList();
        var categoryDomains = config.MemberCategories
            .Where(mc => mc.MemberId == memberId && config.Categories.ContainsKey(mc.Category))
            .SelectMany(mc => config.Categories[mc.Category].Select(d => new CategoryDomain(d, mc.Action, mc.Category)))
            .ToList();
        return new EffectiveUrlPolicy(memberId, member.FilterMode, member.YouTubeMode, memberRules, globalRules, categoryDomains)
        {
            BuiltinBlocks = BuiltinRules.For(config, member),
        };
    }

    /// <summary>
    /// The parents' baseline (PROTECTION-SPEC §13) as a URL policy, for an unrestricted member (<paramref name="memberId"/>)
    /// or an administrator account linked to nobody (null): the family's <see cref="BaselineProtection.AdultCategory"/>
    /// domains blocked, and every active allow rule of the parents — the member's own and the global ones — as member
    /// rules above them, so a parent's explicit allow wins. Nothing else is filtered. A family without the category gets
    /// no blocks. The caller checks <see cref="BaselineProtection.BlockAdult"/>.
    /// </summary>
    public static EffectiveUrlPolicy ForBaseline(FamilyConfig config, string? memberId, DateTimeOffset now)
    {
        var allows = config.Rules.Where(r => (r.MemberId is null || r.MemberId == memberId) && r.Action == RuleAction.Allow && r.IsActiveAt(now)).ToList();
        var adult = config.Categories.TryGetValue(BaselineProtection.AdultCategory, out var domains)
            ? domains.Select(d => new CategoryDomain(d, RuleAction.Block, BaselineProtection.AdultCategory)).ToList()
            : [];
        return new EffectiveUrlPolicy(memberId ?? "", FilterMode.Blocklist, YouTubeMode.Unrestricted, allows, [], adult) { Baseline = true };
    }

    /// <summary>
    /// When the effective policy of <paramref name="memberId"/> (null: global rules only) next changes on its own — the
    /// earliest expiry of an active member or global rule — or <see cref="DateTimeOffset.MaxValue"/> if none expires.
    /// </summary>
    public static DateTimeOffset NextExpiry(FamilyConfig config, string? memberId, DateTimeOffset now) =>
        config.Rules
            .Where(r => (r.MemberId is null || r.MemberId == memberId) && r.ExpiresAt > now)
            .Select(r => r.ExpiresAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MaxValue)
            .Min();

    /// <summary>Always-allowed rules (member and global) — reachable even when time is up.</summary>
    public IEnumerable<Rule> AlwaysAllowedRules => MemberRules.Concat(GlobalRules).Where(r => r.AlwaysAllowed);
}

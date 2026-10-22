namespace WebFilter.Core.Model;

/// <summary>A category domain as it applies to one member.</summary>
public sealed record CategoryDomain(string Domain, RuleAction Action, string Category);

/// <summary>
/// Everything the URL rule evaluator and the DNR compiler need for one member at one instant:
/// active member rules, active global rules, the member's category domains, and the built-in blocks of the member's
/// protection flags (<see cref="BuiltinBlocks"/>). Expired rules are excluded.
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

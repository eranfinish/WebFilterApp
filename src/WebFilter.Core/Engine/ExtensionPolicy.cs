using WebFilter.Core.Model;

namespace WebFilter.Core.Engine;

/// <summary>
/// What the browser extension enforces for one account at one instant: the URL policy its DNR rules are compiled from
/// (null = no rules), the YouTube mode for the content script, whether the time state applies, until when this
/// answer holds on its own (a rule expires, a pause or homework ends), and the extension-side protections
/// (<see cref="Features"/>). Mirrors <see cref="Decider.Url"/>: paused → nothing; the parents' own accounts (administrators
/// linked to nobody, unrestricted members) → the family's baseline protection (<see cref="Baseline"/>), or nothing when it
/// is all off; homework → only "always allowed" and the educational category, YouTube off, plus the member's built-in
/// blocks; otherwise the member's policy.
/// </summary>
public sealed record ExtensionPolicy(EffectiveUrlPolicy? UrlPolicy, YouTubeMode YouTube, bool TimeApplies, DateTimeOffset ValidUntil)
{
    /// <summary>The member's protection flags as the extension applies them (PROTECTION-SPEC §6, <c>RulesResponse.features</c>).</summary>
    public ExtensionFeatures Features { get; init; } = ExtensionFeatures.None;

    /// <summary>
    /// The parents' baseline protection is on for this account (PROTECTION-SPEC §13): the extension is told mode
    /// <c>baseline</c>. False for everyone else, and for parents when all four switches are off (mode <c>unrestricted</c>).
    /// </summary>
    public bool Baseline { get; init; }

    public static ExtensionPolicy For(PolicyView view, DeviceAccount account, DateTimeOffset now)
    {
        var config = view.Config;
        if (account.MemberId is null)
        {
            if (account.IsAdmin) return ForBaseline(view, null, now);
            var always = AlwaysAllowed(config, null, now);
            return new(new EffectiveUrlPolicy("", FilterMode.Allowlist, YouTubeMode.Off, [], always, []), YouTubeMode.Off, false,
                EffectiveUrlPolicy.NextExpiry(config, null, now));
        }

        var member = config.Member(account.MemberId);
        // Before the pause: parents have none, so a pause set for an adult changes nothing.
        if (!member.IsRestricted) return ForBaseline(view, member.Id, now);
        var features = ExtensionFeatures.Of(member);

        var runtime = view.RuntimeOf(member.Id);
        if (view.IsPaused(member.Id, now))
        {
            var local = view.LocalPauses.TryGetValue(member.Id, out var l) && now < l ? l : DateTimeOffset.MinValue;
            var hub = runtime.PausedUntil is { } h && now < h ? h : DateTimeOffset.MinValue;
            // A pause lifts the restrictions (ad blocking included: no filtering at all), not the search log the member is told about.
            return new(null, YouTubeMode.Unrestricted, false, local > hub ? local : hub)
            {
                Features = features with { JumpGuard = false, BlockYouTubeShorts = false, BlockAds = false },
            };
        }

        var nextExpiry = EffectiveUrlPolicy.NextExpiry(config, member.Id, now);
        if (runtime.HomeworkUntil is { } homeworkEnds && now < homeworkEnds)
        {
            var educational = config.Categories.TryGetValue(FamilyConfig.EducationalCategory, out var domains)
                ? domains.Select(d => new CategoryDomain(d, RuleAction.Allow, FamilyConfig.EducationalCategory)).ToList()
                : [];
            var policy = new EffectiveUrlPolicy(member.Id, FilterMode.Allowlist, YouTubeMode.Off,
                AlwaysAllowed(config, member.Id, now), AlwaysAllowed(config, null, now), educational)
            {
                BuiltinBlocks = BuiltinRules.For(config, member),
            };
            return new(policy, YouTubeMode.Off, false, homeworkEnds < nextExpiry ? homeworkEnds : nextExpiry) { Features = features };
        }

        return new(view.UrlPolicy(member.Id, now), member.YouTubeMode, true, nextExpiry) { Features = features };
    }

    /// <summary>
    /// The parents' baseline (PROTECTION-SPEC §13) for an unrestricted member or an administrator linked to nobody (null):
    /// the adult category's rules with <c>blockAdult</c>, the jump guard with "open anyway" and the ad-server ruleset by
    /// the family's switches; no Shorts block, no search log, no time, YouTube unrestricted.
    /// </summary>
    private static ExtensionPolicy ForBaseline(PolicyView view, string? memberId, DateTimeOffset now)
    {
        var baseline = view.Config.Baseline;
        if (!baseline.IsOn) return new(null, YouTubeMode.Unrestricted, false, DateTimeOffset.MaxValue);
        var features = new ExtensionFeatures(baseline.JumpGuard, BlockYouTubeShorts: false, LogSearches: false, CanOverrideJump: true, baseline.BlockAds);
        return baseline.BlockAdult
            ? new(view.BaselinePolicy(memberId, now), YouTubeMode.Unrestricted, false, EffectiveUrlPolicy.NextExpiry(view.Config, memberId, now)) { Features = features, Baseline = true }
            : new(null, YouTubeMode.Unrestricted, false, DateTimeOffset.MaxValue) { Features = features, Baseline = true };
    }

    private static List<Rule> AlwaysAllowed(FamilyConfig config, string? memberId, DateTimeOffset now) =>
        config.Rules.Where(r => r.MemberId == memberId && r.AlwaysAllowed && r.Action == RuleAction.Allow && r.IsActiveAt(now)).ToList();
}

/// <summary>
/// The protections the extension enforces by itself (PROTECTION-SPEC §6–§9, §12). All false for unlinked accounts; for
/// administrators and unrestricted members, the family's baseline protection's (§13) instead of a member's flags.
/// </summary>
/// <param name="JumpGuard">Block a site opening another site in a new tab or window or by an automatic redirect (§7).</param>
/// <param name="BlockYouTubeShorts">Block YouTube /shorts/ in the YouTube logic (§8).</param>
/// <param name="LogSearches">Report searches (<c>reportSearch</c>) and tell the member so (§9).</param>
/// <param name="CanOverrideJump">The jump block page offers "open anyway" (teens, and parents in the baseline).</param>
/// <param name="BlockAds">Enable the extension's static ad-server ruleset (§12).</param>
public sealed record ExtensionFeatures(bool JumpGuard, bool BlockYouTubeShorts, bool LogSearches, bool CanOverrideJump, bool BlockAds)
{
    public static readonly ExtensionFeatures None = new(false, false, false, false, false);

    /// <summary>A restricted member's flags; <see cref="None"/> for an unrestricted one (§1: adults ignore all of them).</summary>
    public static ExtensionFeatures Of(FamilyMember member) =>
        member.IsRestricted ? new(member.JumpGuard, member.BlockYouTubeShorts, member.LogSearches, member.Role == Role.Teen, member.BlockAds) : None;
}

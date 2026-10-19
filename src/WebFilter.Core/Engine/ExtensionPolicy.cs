using WebFilter.Core.Model;

namespace WebFilter.Core.Engine;

/// <summary>
/// What the browser extension enforces for one account at one instant: the URL policy its DNR rules are compiled from
/// (null = no rules), the YouTube mode for the content script, whether the time state applies, and until when this
/// answer holds on its own (a rule expires, a pause or homework ends). Mirrors <see cref="Decider.Url"/>: paused or
/// unrestricted → nothing; homework → only "always allowed" and the educational category, YouTube off; otherwise the
/// member's policy.
/// </summary>
public sealed record ExtensionPolicy(EffectiveUrlPolicy? UrlPolicy, YouTubeMode YouTube, bool TimeApplies, DateTimeOffset ValidUntil)
{
    public static ExtensionPolicy For(PolicyView view, DeviceAccount account, DateTimeOffset now)
    {
        var config = view.Config;
        if (account.MemberId is null)
        {
            if (account.IsAdmin) return new(null, YouTubeMode.Unrestricted, false, DateTimeOffset.MaxValue);
            var always = AlwaysAllowed(config, null, now);
            return new(new EffectiveUrlPolicy("", FilterMode.Allowlist, YouTubeMode.Off, [], always, []), YouTubeMode.Off, false,
                EffectiveUrlPolicy.NextExpiry(config, null, now));
        }

        var member = config.Member(account.MemberId);
        if (!member.IsRestricted) return new(null, YouTubeMode.Unrestricted, false, DateTimeOffset.MaxValue);

        var runtime = view.RuntimeOf(member.Id);
        if (view.IsPaused(member.Id, now))
        {
            var local = view.LocalPauses.TryGetValue(member.Id, out var l) && now < l ? l : DateTimeOffset.MinValue;
            var hub = runtime.PausedUntil is { } h && now < h ? h : DateTimeOffset.MinValue;
            return new(null, YouTubeMode.Unrestricted, false, local > hub ? local : hub);
        }

        var nextExpiry = EffectiveUrlPolicy.NextExpiry(config, member.Id, now);
        if (runtime.HomeworkUntil is { } homeworkEnds && now < homeworkEnds)
        {
            var educational = config.Categories.TryGetValue(FamilyConfig.EducationalCategory, out var domains)
                ? domains.Select(d => new CategoryDomain(d, RuleAction.Allow, FamilyConfig.EducationalCategory)).ToList()
                : [];
            var policy = new EffectiveUrlPolicy(member.Id, FilterMode.Allowlist, YouTubeMode.Off,
                AlwaysAllowed(config, member.Id, now), AlwaysAllowed(config, null, now), educational);
            return new(policy, YouTubeMode.Off, false, homeworkEnds < nextExpiry ? homeworkEnds : nextExpiry);
        }

        return new(view.UrlPolicy(member.Id, now), member.YouTubeMode, true, nextExpiry);
    }

    private static List<Rule> AlwaysAllowed(FamilyConfig config, string? memberId, DateTimeOffset now) =>
        config.Rules.Where(r => r.MemberId == memberId && r.AlwaysAllowed && r.Action == RuleAction.Allow && r.IsActiveAt(now)).ToList();
}

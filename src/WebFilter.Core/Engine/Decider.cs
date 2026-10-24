using WebFilter.Core.Decisions;
using WebFilter.Core.Model;
using WebFilter.Core.Time;
using WebFilter.Core.Urls;
using WebFilter.Core.YouTube;

namespace WebFilter.Core.Engine;

/// <summary>What the content script reports from a YouTube page (spike S4: channel id only from trusted sources).</summary>
public sealed record YouTubePage(string Url, string? ChannelId = null, string? Handle = null);

public sealed record AppIdentity(string? Path = null, string? Package = null, string? Publisher = null);

/// <summary>What the native host may read when the service is down (ProfileCache): restricted or not, and always-allowed rules.</summary>
public sealed record ProfileCacheEntry(bool Restricted, IReadOnlyList<Rule> AlwaysAllowed);

/// <summary>Everything a decision needs: the settings bundle, parent-set runtime state, usage, and device-local pauses.</summary>
public sealed record PolicyView(
    FamilyConfig Config,
    IReadOnlyDictionary<string, MemberRuntime> Runtime,
    UsageLedger Usage,
    IReadOnlyDictionary<string, DateTimeOffset> LocalPauses,
    PolicyCache? Policies = null)
{
    public EffectiveUrlPolicy UrlPolicy(string memberId, DateTimeOffset now) =>
        Policies is not null && ReferenceEquals(Policies.Config, Config) ? Policies.Get(memberId, now) : EffectiveUrlPolicy.For(Config, memberId, now);

    /// <summary>The parents' baseline policy of an unrestricted member, or of administrators linked to nobody (null).</summary>
    public EffectiveUrlPolicy BaselinePolicy(string? memberId, DateTimeOffset now) =>
        Policies is not null && ReferenceEquals(Policies.Config, Config) ? Policies.GetBaseline(memberId, now) : EffectiveUrlPolicy.ForBaseline(Config, memberId, now);

    private static readonly MemberRuntime Empty = new();

    public MemberRuntime RuntimeOf(string memberId) => Runtime.TryGetValue(memberId, out var r) ? r : Empty;

    public bool IsPaused(string memberId, DateTimeOffset now) =>
        RuntimeOf(memberId).IsPaused(now) || (LocalPauses.TryGetValue(memberId, out var until) && now < until);
}

/// <summary>The decision order of MVP-PLAN §6 for URLs, YouTube pages and apps, on top of the pure building blocks.</summary>
public static class Decider
{
    public static TimeStatus Time(PolicyView view, string memberId, DateTimeOffset now)
    {
        var member = view.Config.Member(memberId);
        if (!member.IsRestricted) return new TimeStatus(TimeState.Unlimited, 0, null, null, null, true);
        var zone = view.Config.TimeZone;
        var today = TimeCalculator.LocalDate(now, zone);
        var used = view.Usage.CreditedMinutes(memberId, today, zone, now);
        var grants = view.RuntimeOf(memberId).Grants.Where(g => g.LocalDate == today).ToList();
        return TimeCalculator.Evaluate(new TimeQuery(MemberTimeRules.For(view.Config, memberId), zone, now, used, grants));
    }

    /// <summary>Whether a browsing heartbeat at <paramref name="now"/> counts towards the member's time.</summary>
    public static bool CountsTowardsTime(PolicyView view, string memberId, DateTimeOffset now)
    {
        var member = view.Config.Member(memberId);
        if (!member.IsRestricted || view.IsPaused(memberId, now) || view.RuntimeOf(memberId).IsInHomework(now)) return false;
        return Time(view, memberId, now).BrowsingAllowed;
    }

    public static DecisionResult Url(PolicyView view, string memberId, string rawUrl, DateTimeOffset now)
    {
        if (!UrlNormalizer.IsWebUrl(rawUrl)) return DecisionResult.NotApplicable;
        var member = view.Config.Member(memberId);
        if (!member.IsRestricted) return Baseline(view, memberId, rawUrl, now);
        if (view.IsPaused(memberId, now)) return DecisionResult.Allow(ReasonCode.AllowedPaused);
        if (!UrlNormalizer.TryNormalize(rawUrl, out var url)) return DecisionResult.Block(ReasonCode.BlockedRule);

        var policy = view.UrlPolicy(memberId, now);
        // Built-in blocks (image search, PROTECTION-SPEC §4) hold in every state the member is filtered in, and beat
        // always-allowed and educational sites: the browser installs them above its time-up rules too.
        if (RuleEvaluator.BuiltinBlock(policy, url) is { } builtin) return builtin;
        if (view.RuntimeOf(memberId).IsInHomework(now)) return Homework(view, policy, url);

        if (TimeBlock(view, memberId, now) is { } timeReason)
            return RuleEvaluator.MatchesAlwaysAllowed(policy, url, out var alwaysId)
                ? DecisionResult.Allow(ReasonCode.AllowedAlways, alwaysId)
                : DecisionResult.Block(timeReason);

        if (YouTubeUrlParser.IsYouTubeHost(url.Host))
            return member.YouTubeMode == YouTubeMode.Off && !YouTubeUrlParser.IsSignInHost(url.Host)
                ? DecisionResult.Block(ReasonCode.BlockedYouTubeOff)
                : DecisionResult.Allow(ReasonCode.AllowedDefault);

        return RuleEvaluator.Evaluate(policy, url);
    }

    /// <summary>
    /// The parents' own browsing (PROTECTION-SPEC §13): an unrestricted member's account, or an administrator account linked
    /// to nobody (<paramref name="memberId"/> null). With the family's <see cref="BaselineProtection.BlockAdult"/>, a site
    /// of the <see cref="BaselineProtection.AdultCategory"/> category is blocked (<c>blocked_baseline_adult</c>) unless a
    /// parent's allow rule matches it (then that rule's reason and id); everything else is <c>allowed_unrestricted</c> —
    /// IP addresses, YouTube, any hour. A pause changes nothing: parents have none.
    /// </summary>
    public static DecisionResult Baseline(PolicyView view, string? memberId, string rawUrl, DateTimeOffset now)
    {
        if (!UrlNormalizer.IsWebUrl(rawUrl)) return DecisionResult.NotApplicable;
        if (!view.Config.Baseline.BlockAdult || !UrlNormalizer.TryNormalize(rawUrl, out var url)
            || !RuleEvaluator.IsInCategory(view.Config.Categories, BaselineProtection.AdultCategory, url))
            return DecisionResult.Allow(ReasonCode.AllowedUnrestricted);
        var result = RuleEvaluator.Evaluate(view.BaselinePolicy(memberId, now), url);
        return result.Reason == ReasonCode.AllowedDefault ? DecisionResult.Allow(ReasonCode.AllowedUnrestricted) : result;
    }

    /// <summary>
    /// A non-admin account not yet linked to a family member: only always-allowed global sites — and Google sign-in's
    /// YouTube cookie host, which no YouTube mode blocks (<see cref="YouTubeUrlParser.SignInHost"/>).
    /// </summary>
    public static DecisionResult UnlinkedUrl(PolicyView view, string rawUrl, DateTimeOffset now)
    {
        if (!UrlNormalizer.IsWebUrl(rawUrl)) return DecisionResult.NotApplicable;
        if (!UrlNormalizer.TryNormalize(rawUrl, out var url)) return DecisionResult.Block(ReasonCode.BlockedNotInAllowlist);
        if (YouTubeUrlParser.IsSignInHost(url.Host)) return DecisionResult.Allow(ReasonCode.AllowedDefault);
        var always = view.Config.Rules.Where(r => r.MemberId is null && r.AlwaysAllowed && r.IsActiveAt(now)).ToList();
        var policy = new EffectiveUrlPolicy("", FilterMode.Allowlist, YouTubeMode.Off, [], always, []);
        return RuleEvaluator.Evaluate(policy, url);
    }

    public static DecisionResult YouTube(PolicyView view, string memberId, YouTubePage page, DateTimeOffset now)
    {
        var member = view.Config.Member(memberId);
        if (!member.IsRestricted) return DecisionResult.Allow(ReasonCode.AllowedUnrestricted);
        if (view.IsPaused(memberId, now)) return DecisionResult.Allow(ReasonCode.AllowedPaused);
        if (view.RuntimeOf(memberId).IsInHomework(now)) return DecisionResult.Block(ReasonCode.BlockedHomework);
        if (TimeBlock(view, memberId, now) is { } timeReason) return DecisionResult.Block(timeReason);

        switch (member.YouTubeMode)
        {
            case YouTubeMode.Off:
                return DecisionResult.Block(ReasonCode.BlockedYouTubeOff);
            case YouTubeMode.Unrestricted:
                return DecisionResult.Allow(ReasonCode.AllowedDefault);
        }

        var parsed = YouTubeUrlParser.Parse(page.Url);
        var channelId = ResolveChannel(view.Config, memberId, page, parsed);
        var record = channelId is null ? null : ChannelRecord(view.Config, memberId, channelId);

        if (member.YouTubeMode == YouTubeMode.Restricted)
            return record?.Action == RuleAction.Block
                ? DecisionResult.Block(ReasonCode.BlockedYouTubeChannel)
                : DecisionResult.Allow(ReasonCode.AllowedDefault);

        // Allowlist mode. A member who may search for channels (PROTECTION-SPEC §15) sees YouTube's results restricted to
        // channels on youtube.com (whose other results the extension hides); any other search with terms goes there.
        if (parsed.Kind is YouTubeUrlKind.Search && member.YouTubeChannelSearch && parsed.SearchQuery is not null)
            return parsed.ChannelsOnly && parsed.Site == YouTubeSite.Www
                ? DecisionResult.Allow(ReasonCode.AllowedDefault)
                : DecisionResult.Redirect(ReasonCode.RedirectYouTubeChannelSearch);
        if (parsed.Kind is YouTubeUrlKind.Home or YouTubeUrlKind.Search) return DecisionResult.Redirect(ReasonCode.RedirectYouTubeHome);
        if (parsed.Kind is YouTubeUrlKind.Other or YouTubeUrlKind.Playlist or YouTubeUrlKind.NotYouTube) return DecisionResult.Allow(ReasonCode.AllowedDefault);
        if (channelId is null) return DecisionResult.Block(ReasonCode.BlockedYouTubeUnknown);
        return record?.Action == RuleAction.Allow
            ? DecisionResult.Allow(ReasonCode.AllowedYouTubeChannel)
            : DecisionResult.Block(ReasonCode.BlockedYouTubeChannel);
    }

    public static DecisionResult App(PolicyView view, string memberId, AppIdentity app, DateTimeOffset now)
    {
        var member = view.Config.Member(memberId);
        if (!member.IsRestricted) return DecisionResult.Allow(ReasonCode.AllowedUnrestricted);
        if (view.IsPaused(memberId, now)) return DecisionResult.Allow(ReasonCode.AllowedPaused);
        if (app.Path is { } path && view.Config.SupportedBrowsers.Any(b => string.Equals(b, path, StringComparison.OrdinalIgnoreCase)))
            return DecisionResult.Allow(ReasonCode.AllowedBrowser);

        var rules = view.Config.AppRules.Where(r => (r.MemberId is null || r.MemberId == memberId) && r.IsActiveAt(now) && Matches(r, app)).ToList();
        if (rules.FirstOrDefault(r => r.AlwaysAllowed && r.Action == RuleAction.Allow) is { } always)
            return DecisionResult.Allow(ReasonCode.AllowedAlways, always.Id);
        if (TimeBlock(view, memberId, now) is { } timeReason) return DecisionResult.Block(timeReason);

        // Member rules first, block wins a tie, then global rules.
        foreach (var scope in new[] { rules.Where(r => r.MemberId == memberId), rules.Where(r => r.MemberId is null) })
        {
            var inScope = scope.ToList();
            if (inScope.FirstOrDefault(r => r.Action == RuleAction.Block) is { } block) return DecisionResult.Block(ReasonCode.BlockedAppNetwork, block.Id);
            if (inScope.FirstOrDefault(r => r.Action == RuleAction.Allow) is { } allow) return DecisionResult.Allow(ReasonCode.AllowedRule, allow.Id);
        }
        return DecisionResult.Block(ReasonCode.BlockedAppNetwork);
    }

    /// <summary>The native host's own answer while the service is unreachable (README §9).</summary>
    public static DecisionResult FailClosed(DeviceAccount account, ProfileCacheEntry? cache, string rawUrl)
    {
        if (!UrlNormalizer.IsWebUrl(rawUrl)) return DecisionResult.NotApplicable;
        if (account.IsAdmin) return DecisionResult.Allow(ReasonCode.AllowedUnrestricted);
        if (cache is null) return DecisionResult.Block(ReasonCode.BlockedServiceUnavailable);
        if (!cache.Restricted) return DecisionResult.Allow(ReasonCode.AllowedUnrestricted);
        if (UrlNormalizer.TryNormalize(rawUrl, out var url))
        {
            var policy = new EffectiveUrlPolicy("", FilterMode.Allowlist, YouTubeMode.Off, [], cache.AlwaysAllowed, []);
            if (RuleEvaluator.MatchesAlwaysAllowed(policy, url, out var ruleId)) return DecisionResult.Allow(ReasonCode.AllowedAlways, ruleId);
        }
        return DecisionResult.Block(ReasonCode.BlockedServiceUnavailable);
    }

    public static ProfileCacheEntry ProfileFor(FamilyConfig config, DeviceAccount account, DateTimeOffset now)
    {
        var globalAlways = config.Rules.Where(r => r.MemberId is null && r.AlwaysAllowed && r.IsActiveAt(now));
        if (account.MemberId is null) return new ProfileCacheEntry(true, globalAlways.ToList());
        var member = config.Member(account.MemberId);
        var memberAlways = config.Rules.Where(r => r.MemberId == member.Id && r.AlwaysAllowed && r.IsActiveAt(now));
        return new ProfileCacheEntry(member.IsRestricted, globalAlways.Concat(memberAlways).ToList());
    }

    private static ReasonCode? TimeBlock(PolicyView view, string memberId, DateTimeOffset now) =>
        Time(view, memberId, now).State switch
        {
            TimeState.OutsideWindow => ReasonCode.BlockedTimeWindow,
            TimeState.Expired => ReasonCode.BlockedDailyLimit,
            _ => null,
        };

    private static DecisionResult Homework(PolicyView view, EffectiveUrlPolicy policy, NormalizedUrl url)
    {
        // YouTube pages are blocked during homework (see YouTube()), so the YouTube hosts are too — even when educational
        // or always allowed — or the browser would show a block page for a host the service calls allowed. Google sign-in's
        // YouTube cookie host is never blocked by the YouTube handling, homework included.
        if (YouTubeUrlParser.IsYouTubeHost(url.Host))
            return YouTubeUrlParser.IsSignInHost(url.Host) ? DecisionResult.Allow(ReasonCode.AllowedDefault) : DecisionResult.Block(ReasonCode.BlockedHomework);
        if (RuleEvaluator.MatchesAlwaysAllowed(policy, url, out var alwaysId)) return DecisionResult.Allow(ReasonCode.AllowedAlways, alwaysId);
        return RuleEvaluator.IsInCategory(view.Config.Categories, FamilyConfig.EducationalCategory, url)
            ? DecisionResult.Allow(ReasonCode.AllowedHomework)
            : DecisionResult.Block(ReasonCode.BlockedHomework);
    }

    private static string? ResolveChannel(FamilyConfig config, string memberId, YouTubePage page, YouTubeUrl parsed)
    {
        if (page.ChannelId is { } fromPage) return fromPage;
        if (parsed.ChannelId is { } fromUrl) return fromUrl;
        var handle = (page.Handle ?? parsed.Handle)?.ToLowerInvariant();
        if (handle is null) return null;
        return config.YouTubeChannels
            .Where(c => c.MemberId is null || c.MemberId == memberId)
            .FirstOrDefault(c => string.Equals(c.Handle, handle, StringComparison.OrdinalIgnoreCase))?.ChannelId;
    }

    private static YouTubeChannel? ChannelRecord(FamilyConfig config, string memberId, string channelId) =>
        config.YouTubeChannels.FirstOrDefault(c => c.MemberId == memberId && c.ChannelId == channelId)
        ?? config.YouTubeChannels.FirstOrDefault(c => c.MemberId is null && c.ChannelId == channelId);

    private static bool Matches(AppRule rule, AppIdentity app) => rule.Match switch
    {
        AppMatchType.Path => app.Path is not null && string.Equals(rule.Value, app.Path, StringComparison.OrdinalIgnoreCase),
        AppMatchType.Package => app.Package is not null && string.Equals(rule.Value, app.Package, StringComparison.OrdinalIgnoreCase),
        AppMatchType.Publisher => app.Publisher is not null && string.Equals(rule.Value, app.Publisher, StringComparison.OrdinalIgnoreCase),
        _ => false,
    };
}

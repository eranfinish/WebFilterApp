namespace WebFilter.Core.Decisions;

public enum Decision { Allow, Block, Redirect, NotApplicable }

/// <summary>The closed list of reason codes shared by code, schema, plan and scenarios (CLAUDE.md rule 8).</summary>
public enum ReasonCode
{
    None,
    AllowedUnrestricted,
    AllowedPaused,
    AllowedAlways,
    AllowedRule,
    AllowedCategory,
    AllowedDefault,
    AllowedHomework,
    AllowedYouTubeChannel,
    AllowedBrowser,
    BlockedRule,
    BlockedCategory,
    BlockedNotInAllowlist,
    BlockedTimeWindow,
    BlockedDailyLimit,
    BlockedHomework,
    BlockedIpLiteral,
    BlockedYouTubeChannel,
    BlockedYouTubeUnknown,
    BlockedYouTubeOff,
    BlockedServiceUnavailable,
    BlockedAppNetwork,
    RedirectYouTubeHome,
}

public readonly record struct DecisionResult(Decision Decision, ReasonCode Reason, string? RuleId = null)
{
    public static DecisionResult Allow(ReasonCode reason, string? ruleId = null) => new(Decision.Allow, reason, ruleId);
    public static DecisionResult Block(ReasonCode reason, string? ruleId = null) => new(Decision.Block, reason, ruleId);
    public static DecisionResult Redirect(ReasonCode reason) => new(Decision.Redirect, reason);
    public static readonly DecisionResult NotApplicable = new(Decision.NotApplicable, ReasonCode.None);

    public bool IsAllowed => Decision == Decision.Allow;

    public override string ToString() => RuleId is null ? $"{Decision}/{Reason.ToWire()}" : $"{Decision}/{Reason.ToWire()}/{RuleId}";
}

public static class ReasonCodes
{
    private static readonly Dictionary<ReasonCode, string> ToWireMap = new()
    {
        [ReasonCode.None] = "",
        [ReasonCode.AllowedUnrestricted] = "allowed_unrestricted",
        [ReasonCode.AllowedPaused] = "allowed_paused",
        [ReasonCode.AllowedAlways] = "allowed_always",
        [ReasonCode.AllowedRule] = "allowed_rule",
        [ReasonCode.AllowedCategory] = "allowed_category",
        [ReasonCode.AllowedDefault] = "allowed_default",
        [ReasonCode.AllowedHomework] = "allowed_homework",
        [ReasonCode.AllowedYouTubeChannel] = "allowed_youtube_channel",
        [ReasonCode.AllowedBrowser] = "allowed_browser",
        [ReasonCode.BlockedRule] = "blocked_rule",
        [ReasonCode.BlockedCategory] = "blocked_category",
        [ReasonCode.BlockedNotInAllowlist] = "blocked_not_in_allowlist",
        [ReasonCode.BlockedTimeWindow] = "blocked_time_window",
        [ReasonCode.BlockedDailyLimit] = "blocked_daily_limit",
        [ReasonCode.BlockedHomework] = "blocked_homework",
        [ReasonCode.BlockedIpLiteral] = "blocked_ip_literal",
        [ReasonCode.BlockedYouTubeChannel] = "blocked_youtube_channel",
        [ReasonCode.BlockedYouTubeUnknown] = "blocked_youtube_unknown",
        [ReasonCode.BlockedYouTubeOff] = "blocked_youtube_off",
        [ReasonCode.BlockedServiceUnavailable] = "blocked_service_unavailable",
        [ReasonCode.BlockedAppNetwork] = "blocked_app_network",
        [ReasonCode.RedirectYouTubeHome] = "redirect_youtube_home",
    };

    private static readonly Dictionary<string, ReasonCode> FromWireMap = ToWireMap.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string ToWire(this ReasonCode reason) => ToWireMap[reason];

    public static ReasonCode Parse(string wire) =>
        FromWireMap.TryGetValue(wire, out var reason) ? reason : throw new ArgumentException($"Unknown reason code '{wire}'", nameof(wire));

    public static IReadOnlyCollection<string> AllWire => FromWireMap.Keys;
}

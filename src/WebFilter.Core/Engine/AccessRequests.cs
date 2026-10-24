namespace WebFilter.Core.Engine;

public enum RequestKind { Url, YouTube, App, Time }

public enum RequestStatus { Pending, Approved, Denied }

public enum RequestResult { Created, Merged, RateLimited, Cooldown, Queued, Unavailable }

public enum ApproveScope { Host, Page }

/// <summary>How long an approval lasts. <see cref="Minutes"/> applies to <see cref="DurationKind.Minutes"/> only.</summary>
public readonly record struct ApprovalDuration(DurationKind Kind, int Minutes = 0)
{
    public static ApprovalDuration For(int minutes) => new(DurationKind.Minutes, minutes);
    public static readonly ApprovalDuration Today = new(DurationKind.Today);
    public static readonly ApprovalDuration Always = new(DurationKind.Always);
}

public enum DurationKind { Minutes, Today, Always }

public sealed class AccessRequest
{
    public required string Id { get; init; }
    public required string MemberId { get; init; }
    public required string DeviceId { get; init; }
    public required RequestKind Kind { get; init; }
    /// <summary>url: the full URL · youtube: channel id · app: path or package · time: empty.</summary>
    public required string Target { get; init; }
    /// <summary>What merging and cooldown compare: host without "www." · channel id · lowercase path · "time:{date}".</summary>
    public required string TargetKey { get; init; }
    public int? Minutes { get; init; }
    public string? Message { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public DateTimeOffset? DecidedAt { get; set; }
    public string? CreatedRuleId { get; set; }

    public AccessRequest Clone() => (AccessRequest)MemberwiseClone();
}

/// <summary>
/// A parent's decision on a request, as the member's browsers hear of it (accessDecided): raised on the hub when the
/// parent decides, and carried in the hub's snapshots (<see cref="HubSnapshot.Decisions"/>) so an agent can tell the
/// browsers on its computer too (MVP-PLAN §8.1).
/// </summary>
/// <param name="DeviceId">The computer the request came from.</param>
/// <param name="Until">When the approval ends: the expiry of the rule it created. Null for a denial and for an approval that does not end.</param>
public sealed record RequestDecision(
    string RequestId,
    string MemberId,
    string DeviceId,
    RequestKind Kind,
    string TargetKey,
    RequestStatus Status,
    DateTimeOffset DecidedAt,
    DateTimeOffset? Until);

public static class AccessRequestRules
{
    public const int MaxNewPerWindow = 5;
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(60);

    /// <summary>How long a decision travels in the hub's snapshots, so an agent that was off or unreachable still hears of it.</summary>
    public static readonly TimeSpan DecisionWindow = TimeSpan.FromHours(48);

    /// <summary>At most this many (the newest) decisions travel in a snapshot.</summary>
    public const int MaxDecisionsSent = 100;
}

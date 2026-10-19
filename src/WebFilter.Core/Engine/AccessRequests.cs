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

public static class AccessRequestRules
{
    public const int MaxNewPerWindow = 5;
    public static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(60);
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(60);
}

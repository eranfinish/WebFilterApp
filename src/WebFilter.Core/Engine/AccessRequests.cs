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

    /// <summary>youtube: the channel's title as the child's browser saw it (PROTECTION-SPEC §15); null when unknown.</summary>
    public string? Title { get; set; }

    /// <summary>youtube: the channel's handle ("@name", lower case); null when unknown.</summary>
    public string? Handle { get; set; }

    /// <summary>youtube: the channel search the child found the channel with, when it came from one.</summary>
    public string? SearchQuery { get; set; }

    public AccessRequest Clone() => (AccessRequest)MemberwiseClone();
}

/// <summary>
/// The names a YouTube request carries (PROTECTION-SPEC §15). They come from the child's browser, which read them from
/// YouTube's page, so they are only shown to the parents and stored with an approved channel — never matched or trusted
/// otherwise — and are cleaned on the way in: trimmed, no control characters, of bounded length, a handle in the form
/// "@name" in lower case. Anything that does not fit is dropped (null).
/// </summary>
public static class RequestNames
{
    public const int MaxTitle = 100, MaxHandle = 101, MaxSearchQuery = 200;

    public static string? Title(string? value) => Clean(value, MaxTitle);

    public static string? SearchQuery(string? value) => Clean(value, MaxSearchQuery);

    /// <summary>"@name" in lower case: letters and digits of any script, '.', '_', '-' and '·'; anything else is not a handle.</summary>
    public static string? Handle(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        if (text[0] != '@') text = "@" + text;
        if (text.Length is < 2 or > MaxHandle) return null;
        foreach (var c in text.AsSpan(1))
            if (!char.IsLetterOrDigit(c) && c is not ('.' or '_' or '-' or '·')) return null;
        return text.ToLowerInvariant();
    }

    /// <summary>
    /// Whitespace and control characters become single spaces; direction marks and overrides (format characters, except
    /// the joiners emoji need) are dropped, so a name cannot turn the text around it in the parents' screens.
    /// </summary>
    private static string? Clean(string? value, int max)
    {
        if (value is null) return null;
        var text = new string(value
            .Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format || c is '‌' or '‍')
            .Select(c => char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c).ToArray());
        text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) return null;
        return text.Length <= max ? text : text[..max].TrimEnd();
    }
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

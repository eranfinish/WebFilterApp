using WebFilter.Core.Time;

namespace WebFilter.Core.Engine;

/// <summary>Short-lived per-member state set by parents: pause, homework mode and extra time.</summary>
public sealed class MemberRuntime
{
    public DateTimeOffset? PausedUntil { get; set; }
    public DateTimeOffset? HomeworkUntil { get; set; }
    public List<TimeGrant> Grants { get; init; } = [];

    public bool IsPaused(DateTimeOffset now) => PausedUntil is { } until && now < until;
    public bool IsInHomework(DateTimeOffset now) => HomeworkUntil is { } until && now < until;

    public MemberRuntime Clone() => new() { PausedUntil = PausedUntil, HomeworkUntil = HomeworkUntil, Grants = [.. Grants] };
}

using WebFilter.Core.Model;

namespace WebFilter.Core.Time;

public enum TimeState { Ok, Warn, Expired, OutsideWindow, Unlimited }

/// <summary>The time rules of one member (from <see cref="FamilyConfig.Schedules"/> and <see cref="FamilyConfig.DailyLimits"/>).</summary>
public sealed record MemberTimeRules(IReadOnlyList<Schedule> Schedules, IReadOnlyList<DailyLimit> Limits)
{
    public static MemberTimeRules For(FamilyConfig config, string memberId) => new(
        config.Schedules.Where(s => s.MemberId == memberId).ToList(),
        config.DailyLimits.Where(l => l.MemberId == memberId).ToList());
}

/// <summary>
/// Extra time granted for one local date (extendTime / approved time request): <see cref="ExtraMinutes"/> raise
/// that day's limit; an optional temporary window <c>[TempWindowStart, TempWindowEnd)</c> (local minutes of day) is added.
/// </summary>
public sealed record TimeGrant(DateOnly LocalDate, int ExtraMinutes, int? TempWindowStart = null, int? TempWindowEnd = null);

/// <param name="UsedMinutes">Credited minutes for the local date of <paramref name="Now"/> (see <see cref="UsageLedger"/>).</param>
public sealed record TimeQuery(MemberTimeRules Rules, TimeZoneInfo Zone, DateTimeOffset Now, int UsedMinutes, IReadOnlyList<TimeGrant> Grants);

/// <param name="LimitMinutes">Today's limit including grants; null when there is no limit today.</param>
/// <param name="RemainingMinutes">max(0, limit − used); null when there is no limit.</param>
/// <param name="WindowEndsAtMinute">End (local minutes of day) of the window containing now, after merging schedules and
/// temporary windows; null when outside any window or when the member has no schedules at all.</param>
public sealed record TimeStatus(
    TimeState State,
    int UsedMinutes,
    int? LimitMinutes,
    int? RemainingMinutes,
    int? WindowEndsAtMinute,
    bool InWindow)
{
    public bool BrowsingAllowed => State is TimeState.Ok or TimeState.Warn or TimeState.Unlimited;
}

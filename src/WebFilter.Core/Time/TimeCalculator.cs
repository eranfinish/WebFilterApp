namespace WebFilter.Core.Time;

/// <summary>
/// Pure time-rule evaluation (tests/simulation/README.md §6). All instants are absolute; local dates, days of week and
/// minutes of day come from the family time zone (never the system time zone), so DST transitions are handled by
/// <see cref="TimeZoneInfo"/>.
/// </summary>
public static class TimeCalculator
{
    /// <summary>
    /// Window: if the member has any schedule, now must fall in <c>[start, end)</c> of a schedule for today's local day of
    /// week or of a temporary window granted for today; a day without windows has none. No schedules at all = no window
    /// restriction (WindowEndsAtMinute null). Limit: today's limit + today's grants. State: OutsideWindow if not in a
    /// window; else Unlimited if no limit; else Expired if used ≥ limit; Warn if remaining ≤ 10; else Ok.
    /// Minute-of-day is the local time floored to the minute.
    /// </summary>
    public static TimeStatus Evaluate(TimeQuery query) => throw new NotImplementedException();

    /// <summary>
    /// Builds the grant for "give N more minutes now" (README §6 extendTime): N extra minutes for today; if now is outside
    /// every window, or the current window ends in fewer than N minutes, also a temporary window [now, now+N) for today
    /// (capped at 1440).
    /// </summary>
    public static TimeGrant CreateExtension(MemberTimeRules rules, TimeZoneInfo zone, DateTimeOffset now, int minutes, IReadOnlyList<TimeGrant> existingGrants) =>
        throw new NotImplementedException();

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) => throw new NotImplementedException();

    /// <summary>Local minutes since midnight, floored (0–1439).</summary>
    public static int LocalMinuteOfDay(DateTimeOffset instant, TimeZoneInfo zone) => throw new NotImplementedException();

    /// <summary>The first instant of the next local day (handles 23- and 25-hour days).</summary>
    public static DateTimeOffset NextLocalMidnight(DateTimeOffset instant, TimeZoneInfo zone) => throw new NotImplementedException();
}

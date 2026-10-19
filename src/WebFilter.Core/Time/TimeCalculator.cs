namespace WebFilter.Core.Time;

/// <summary>
/// Pure time-rule evaluation (tests/simulation/README.md §6). All instants are absolute; local dates, days of week and
/// minutes of day come from the family time zone (never the system time zone), so DST transitions are handled by
/// <see cref="TimeZoneInfo"/>.
/// </summary>
public static class TimeCalculator
{
    private const int MinutesPerDay = 24 * 60;
    private const int WarnAtRemainingMinutes = 10;

    /// <summary>
    /// Window: if the member has any schedule, now must fall in <c>[start, end)</c> of a schedule for today's local day of
    /// week or of a temporary window granted for today; a day without windows has none. No schedules at all = no window
    /// restriction (WindowEndsAtMinute null). Limit: today's limit + today's grants. State: OutsideWindow if not in a
    /// window; else Unlimited if no limit; else Expired if used ≥ limit; Warn if remaining ≤ 10; else Ok.
    /// Minute-of-day is the local time floored to the minute.
    /// </summary>
    public static TimeStatus Evaluate(TimeQuery query)
    {
        var local = TimeZoneInfo.ConvertTime(query.Now, query.Zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var todaysGrants = query.Grants.Where(g => g.LocalDate == today).ToList();

        var (inWindow, windowEnd) = FindWindow(query.Rules, local.DayOfWeek, MinuteOfDay(local), todaysGrants);

        int? limit = TodaysLimit(query.Rules, local.DayOfWeek) is { } baseLimit
            ? baseLimit + todaysGrants.Sum(g => g.ExtraMinutes)
            : null;
        int? remaining = limit is { } l ? Math.Max(0, l - query.UsedMinutes) : null;

        var state = !inWindow ? TimeState.OutsideWindow
            : limit is null ? TimeState.Unlimited
            : query.UsedMinutes >= limit ? TimeState.Expired
            : remaining <= WarnAtRemainingMinutes ? TimeState.Warn
            : TimeState.Ok;

        return new TimeStatus(state, query.UsedMinutes, limit, remaining, windowEnd, inWindow);
    }

    /// <summary>
    /// Builds the grant for "give N more minutes now" (README §6 extendTime): N extra minutes for today; if now is outside
    /// every window, or the current window ends in fewer than N minutes, also a temporary window [now, now+N) for today
    /// (capped at 1440).
    /// </summary>
    public static TimeGrant CreateExtension(MemberTimeRules rules, TimeZoneInfo zone, DateTimeOffset now, int minutes, IReadOnlyList<TimeGrant> existingGrants)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minutes);

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var minute = MinuteOfDay(local);
        var (inWindow, windowEnd) = FindWindow(rules, local.DayOfWeek, minute, existingGrants.Where(g => g.LocalDate == today));

        // Without any schedule the hours are not restricted, so a temporary window would change nothing.
        var needsWindow = rules.Schedules.Count > 0 && (!inWindow || windowEnd!.Value - minute < minutes);
        return needsWindow
            ? new TimeGrant(today, minutes, minute, Math.Min(minute + minutes, MinutesPerDay))
            : new TimeGrant(today, minutes);
    }

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>Local minutes since midnight, floored (0–1439).</summary>
    public static int LocalMinuteOfDay(DateTimeOffset instant, TimeZoneInfo zone) =>
        MinuteOfDay(TimeZoneInfo.ConvertTime(instant, zone));

    /// <summary>The first instant of the next local day (handles 23- and 25-hour days).</summary>
    public static DateTimeOffset NextLocalMidnight(DateTimeOffset instant, TimeZoneInfo zone) =>
        StartOfLocalDay(LocalDate(instant, zone).AddDays(1), zone);

    /// <summary>
    /// The first instant of a local date: midnight (its first occurrence if a backward transition repeats it), or the
    /// end of the gap if a forward transition skips it.
    /// </summary>
    internal static DateTimeOffset StartOfLocalDay(DateOnly date, TimeZoneInfo zone)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        if (zone.IsAmbiguousTime(midnight))
            return new DateTimeOffset(midnight, zone.GetAmbiguousTimeOffsets(midnight).Max());

        // The first valid local time after a gap is the transition instant, i.e. when the day really begins.
        while (zone.IsInvalidTime(midnight))
            midnight = midnight.AddMinutes(1);
        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    private static int MinuteOfDay(DateTimeOffset local) => local.Hour * 60 + local.Minute;

    /// <summary>The smallest limit configured for the day (normally there is at most one), or null.</summary>
    private static int? TodaysLimit(MemberTimeRules rules, DayOfWeek day) =>
        rules.Limits.Where(l => l.Days.Contains(day)).Select(l => (int?)l.Minutes).Min();

    /// <summary>
    /// Whether <paramref name="minute"/> is inside today's windows (schedules plus temporary windows, merged when they
    /// overlap or touch), and the end of the merged window containing it. No schedules at all = no restriction.
    /// </summary>
    private static (bool InWindow, int? EndsAt) FindWindow(MemberTimeRules rules, DayOfWeek day, int minute, IEnumerable<TimeGrant> todaysGrants)
    {
        if (rules.Schedules.Count == 0)
            return (true, null);

        var intervals = rules.Schedules
            .Where(s => s.Days.Contains(day))
            .Select(s => (Start: s.StartMinute, End: s.EndMinute))
            .Concat(todaysGrants
                .Where(g => g.TempWindowStart is not null && g.TempWindowEnd is not null)
                .Select(g => (Start: g.TempWindowStart!.Value, End: g.TempWindowEnd!.Value)))
            .Select(i => (Start: Math.Max(0, i.Start), End: Math.Min(MinutesPerDay, i.End)))
            .Where(i => i.Start < i.End)
            .OrderBy(i => i.Start);

        var merged = new List<(int Start, int End)>();
        foreach (var interval in intervals)
        {
            if (merged.Count > 0 && interval.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, interval.End));
            else
                merged.Add(interval);
        }

        foreach (var (start, end) in merged)
        {
            if (start <= minute && minute < end)
                return (true, end);
        }
        return (false, null);
    }
}

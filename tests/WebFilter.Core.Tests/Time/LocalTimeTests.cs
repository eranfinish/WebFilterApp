using WebFilter.Core.Time;
using static WebFilter.Core.Tests.Time.TimeFixture;

namespace WebFilter.Core.Tests.Time;

/// <summary>Local dates, minutes and midnights in the family zone, including DST transitions.</summary>
public class LocalTimeTests
{
    // Israel leaves DST on Sunday 2026-10-25 at 02:00 (+03:00 → +02:00), i.e. at 2026-10-24T23:00Z.
    [Fact]
    public void ZoneData_MatchesTheFixtureAssumptions()
    {
        Assert.Equal(TimeSpan.FromHours(3), Zone.GetUtcOffset(Utc(2026, 10, 24, 22, 59)));
        Assert.Equal(TimeSpan.FromHours(2), Zone.GetUtcOffset(Utc(2026, 10, 24, 23, 0)));
        Assert.Equal(DayOfWeek.Sunday, new DateOnly(2026, 11, 1).DayOfWeek);
    }

    [Fact]
    public void LocalDateAndMinute_UseTheFamilyZone()
    {
        var instant = Utc(2026, 11, 1, 22, 30); // 00:30 on Monday 2026-11-02 in Israel

        Assert.Equal(new DateOnly(2026, 11, 2), TimeCalculator.LocalDate(instant, Zone));
        Assert.Equal(30, TimeCalculator.LocalMinuteOfDay(instant, Zone));
    }

    [Theory]
    [InlineData("2026-11-01T00:00:00", 0)]
    [InlineData("2026-11-01T15:00:59", 900)]
    [InlineData("2026-11-01T23:59:59", 1439)]
    public void LocalMinuteOfDay_IsFloored(string localTime, int expected)
    {
        Assert.Equal(expected, TimeCalculator.LocalMinuteOfDay(Local(localTime), Zone));
    }

    [Fact]
    public void RepeatedHour_MapsBothOccurrencesToTheSameWallClock()
    {
        var first = Utc(2026, 10, 24, 22, 30);  // 01:30+03:00
        var second = Utc(2026, 10, 24, 23, 30); // 01:30+02:00

        Assert.Equal(90, TimeCalculator.LocalMinuteOfDay(first, Zone));
        Assert.Equal(90, TimeCalculator.LocalMinuteOfDay(second, Zone));
        Assert.Equal(new DateOnly(2026, 10, 25), TimeCalculator.LocalDate(first, Zone));
        Assert.Equal(new DateOnly(2026, 10, 25), TimeCalculator.LocalDate(second, Zone));
    }

    // TIME-010.
    [Fact]
    public void Dst_WindowsFollowWallClockTime()
    {
        // Saturday 2026-10-24 10:30, still UTC+3: noa's weekend window is 09:00-20:00.
        var saturday = Evaluate(Noa, new DateTimeOffset(2026, 10, 24, 10, 30, 0, TimeSpan.FromHours(3)), used: 30);
        Assert.Equal(new TimeStatus(TimeState.Ok, 30, 180, 150, Minute("20:00"), true), saturday);

        // Sunday 14:30 local is 12:30Z; an engine that kept +03:00 would see 15:30 and allow it.
        Assert.Equal(TimeState.OutsideWindow, Evaluate(Noa, Utc(2026, 10, 25, 12, 30)).State);
        // The same instant written with the stale offset.
        Assert.Equal(TimeState.OutsideWindow, Evaluate(Noa, new DateTimeOffset(2026, 10, 25, 15, 30, 0, TimeSpan.FromHours(3))).State);

        var sunday = Evaluate(Noa, Utc(2026, 10, 25, 13, 0)); // 15:00 local
        Assert.Equal(new TimeStatus(TimeState.Ok, 0, 120, 120, Minute("19:00"), true), sunday);
    }

    [Fact]
    public void Dst_LedgerAndCalculatorTogether()
    {
        var ledger = new UsageLedger();
        Browse(ledger, "noa", "pc-kids", "2026-10-24T10:00", "2026-10-24T10:30");
        var saturday = Local("2026-10-24T10:30");
        Assert.Equal(30, Used(ledger, "noa", saturday));

        var sundayStart = Local("2026-10-25T15:00");
        Assert.Equal(0, Used(ledger, "noa", sundayStart));

        Browse(ledger, "noa", "pc-kids", sundayStart, Local("2026-10-25T16:00"));
        var now = Local("2026-10-25T16:00");
        var status = Evaluate(Noa, now, Used(ledger, "noa", now));

        Assert.Equal(new TimeStatus(TimeState.Ok, 60, 120, 60, Minute("19:00"), true), status);
    }

    [Fact]
    public void NextLocalMidnight_OnAnOrdinaryDay()
    {
        var next = TimeCalculator.NextLocalMidnight(Local("2026-11-05T18:00"), Zone);

        Assert.Equal(Local("2026-11-06T00:00"), next);
        Assert.Equal(TimeSpan.FromHours(24), TimeCalculator.NextLocalMidnight(next, Zone) - next);
    }

    [Fact]
    public void NextLocalMidnight_AtMidnight_IsTheFollowingOne()
    {
        Assert.Equal(Local("2026-11-02T00:00"), TimeCalculator.NextLocalMidnight(Local("2026-11-01T00:00"), Zone));
    }

    [Fact]
    public void NextLocalMidnight_HandlesThe25HourDay()
    {
        var dayStart = TimeCalculator.NextLocalMidnight(Local("2026-10-24T12:00"), Zone);
        var dayEnd = TimeCalculator.NextLocalMidnight(Local("2026-10-25T12:00"), Zone);

        Assert.Equal(Utc(2026, 10, 24, 21, 0), dayStart); // 00:00+03:00
        Assert.Equal(Utc(2026, 10, 25, 22, 0), dayEnd);   // 00:00+02:00
        Assert.Equal(TimeSpan.FromHours(25), dayEnd - dayStart);

        // From either occurrence of the repeated hour.
        Assert.Equal(dayEnd, TimeCalculator.NextLocalMidnight(Utc(2026, 10, 24, 22, 30), Zone));
        Assert.Equal(dayEnd, TimeCalculator.NextLocalMidnight(Utc(2026, 10, 24, 23, 30), Zone));

        Assert.Equal(new DateOnly(2026, 10, 26), TimeCalculator.LocalDate(dayEnd, Zone));
        Assert.Equal(0, TimeCalculator.LocalMinuteOfDay(dayEnd, Zone));
        Assert.Equal(new DateOnly(2026, 10, 25), TimeCalculator.LocalDate(dayEnd.AddTicks(-1), Zone));
    }

    // Israel enters DST on Friday 2026-03-27 at 02:00 (+02:00 → +03:00).
    [Fact]
    public void NextLocalMidnight_HandlesThe23HourDay()
    {
        var dayStart = TimeCalculator.NextLocalMidnight(Local("2026-03-26T12:00"), Zone);
        var dayEnd = TimeCalculator.NextLocalMidnight(Local("2026-03-27T12:00"), Zone);

        Assert.Equal(Utc(2026, 3, 26, 22, 0), dayStart);
        Assert.Equal(Utc(2026, 3, 27, 21, 0), dayEnd);
        Assert.Equal(TimeSpan.FromHours(23), dayEnd - dayStart);
    }

    [Fact]
    public void NextLocalMidnight_WhenMidnightIsSkipped_IsTheEndOfTheGap()
    {
        var zone = MidnightTransitionZone();
        Assert.True(zone.IsInvalidTime(new DateTime(2026, 4, 5, 0, 30, 0)));

        var next = TimeCalculator.NextLocalMidnight(new DateTimeOffset(2026, 4, 4, 12, 0, 0, TimeSpan.FromHours(-3)), zone);

        Assert.Equal(Utc(2026, 4, 5, 3, 0), next); // 00:00-03:00 = 01:00-02:00
        Assert.Equal(new DateOnly(2026, 4, 5), TimeCalculator.LocalDate(next, zone));
        Assert.Equal(60, TimeCalculator.LocalMinuteOfDay(next, zone));
        Assert.Equal(new DateOnly(2026, 4, 4), TimeCalculator.LocalDate(next.AddTicks(-1), zone));
    }

    [Fact]
    public void NextLocalMidnight_WhenMidnightRepeats_IsItsFirstOccurrence()
    {
        var zone = MidnightTransitionZone();
        Assert.True(zone.IsAmbiguousTime(new DateTime(2026, 10, 4, 0, 30, 0)));

        var dayStart = TimeCalculator.NextLocalMidnight(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.FromHours(-2)), zone);
        Assert.Equal(Utc(2026, 10, 4, 2, 0), dayStart); // 00:00-02:00

        // The second 00:30 (-03:00) is still on 2026-10-04, whose day lasts 25 hours.
        var secondOccurrence = Utc(2026, 10, 4, 3, 30);
        Assert.Equal(new DateOnly(2026, 10, 4), TimeCalculator.LocalDate(secondOccurrence, zone));
        Assert.Equal(TimeSpan.FromHours(25), TimeCalculator.NextLocalMidnight(secondOccurrence, zone) - dayStart);
    }

    // TIME-009: Thursday 2026-11-05 (240) is used up; Friday 2026-11-06 (300) starts from zero.
    [Fact]
    public void Usage_ResetsAtLocalMidnight_AndTheNextDaysLimitApplies()
    {
        var ledger = new UsageLedger();
        Browse(ledger, "yoav", "laptop-yoav", "2026-11-05T14:00", "2026-11-05T18:00");

        var thursday = Local("2026-11-05T18:00");
        Assert.Equal(new TimeStatus(TimeState.Expired, 240, 240, 0, Minute("21:30"), true), Evaluate(Yoav, thursday, Used(ledger, "yoav", thursday)));

        var midnight = TimeCalculator.NextLocalMidnight(thursday, Zone);
        Assert.Equal(0, Used(ledger, "yoav", midnight));
        Assert.Equal(240, Used(ledger, "yoav", midnight.AddTicks(-1)));

        var friday = Local("2026-11-06T09:00");
        Assert.Equal(new TimeStatus(TimeState.Ok, 0, 300, 300, Minute("23:00"), true), Evaluate(Yoav, friday, Used(ledger, "yoav", friday)));
    }

    /// <summary>UTC−3 with DST (+1 h) from the first Sunday of April at 00:00 to the first Sunday of October at 01:00 DST.</summary>
    private static TimeZoneInfo MidnightTransitionZone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 0, 0, 0), 4, 1, DayOfWeek.Sunday),
            TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 1, DayOfWeek.Sunday));
        return TimeZoneInfo.CreateCustomTimeZone("Test/MidnightDst", TimeSpan.FromHours(-3), "Test", "Test", "Test DST", [rule]);
    }
}

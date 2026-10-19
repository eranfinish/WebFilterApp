using WebFilter.Core.Model;
using WebFilter.Core.Time;
using static WebFilter.Core.Tests.Time.TimeFixture;

namespace WebFilter.Core.Tests.Time;

public class TimeCalculatorEvaluateTests
{
    // TIME-001: 2026-11-01 is a Sunday, noa's window is 15:00-19:00.
    [Theory]
    [InlineData("2026-11-01T14:59", false)]
    [InlineData("2026-11-01T15:00", true)]
    [InlineData("2026-11-01T18:59", true)]
    [InlineData("2026-11-01T19:00", false)]
    public void Window_IncludesStartMinute_ExcludesEndMinute(string localNow, bool inside)
    {
        var status = Evaluate(Noa, localNow);

        Assert.Equal(inside, status.InWindow);
        Assert.Equal(inside ? TimeState.Ok : TimeState.OutsideWindow, status.State);
        Assert.Equal(inside ? Minute("19:00") : (int?)null, status.WindowEndsAtMinute);
        Assert.Equal(inside, status.BrowsingAllowed);
    }

    [Fact]
    public void Window_AtStart_ReportsFullLimit()
    {
        var status = Evaluate(Noa, "2026-11-01T15:00");

        Assert.Equal(new TimeStatus(TimeState.Ok, 0, 120, 120, Minute("19:00"), true), status);
    }

    [Theory]
    [InlineData("2026-11-01T14:59:59", false)]
    [InlineData("2026-11-01T18:59:59", true)]
    public void Window_UsesLocalTimeFlooredToTheMinute(string localNow, bool inside)
    {
        Assert.Equal(inside, Evaluate(Noa, localNow).InWindow);
    }

    // TIME-002: 2026-11-06 is a Friday, noa has 09:00-13:00 and 16:00-20:00.
    [Theory]
    [InlineData("08:59", null)]
    [InlineData("09:00", "13:00")]
    [InlineData("12:59", "13:00")]
    [InlineData("13:00", null)]
    [InlineData("13:30", null)]
    [InlineData("15:59", null)]
    [InlineData("16:00", "20:00")]
    [InlineData("19:59", "20:00")]
    [InlineData("20:00", null)]
    public void TwoWindowsOnFriday(string localTime, string? windowEnd)
    {
        var status = Evaluate(Noa, $"2026-11-06T{localTime}");

        Assert.Equal(windowEnd is not null, status.InWindow);
        Assert.Equal(windowEnd is null ? (int?)null : Minute(windowEnd), status.WindowEndsAtMinute);
        Assert.Equal(windowEnd is null ? TimeState.OutsideWindow : TimeState.Ok, status.State);
        Assert.Equal(180, status.LimitMinutes);
    }

    [Fact]
    public void OutsideWindow_StillReportsLimitAndRemaining()
    {
        var status = Evaluate(Noa, "2026-11-06T13:30", used: 40);

        Assert.Equal(new TimeStatus(TimeState.OutsideWindow, 40, 180, 140, null, false), status);
    }

    // TIME-013: yoav keeps only his Sunday-Thursday window; 2026-11-06 is a Friday.
    [Fact]
    public void DayWithoutWindows_HasNoBrowsing()
    {
        var rules = Yoav with { Schedules = [new Schedule("yoav", Days(0, 1, 2, 3, 4), Minute("14:00"), Minute("21:30"))] };

        var status = Evaluate(rules, "2026-11-06T10:00");

        Assert.Equal(TimeState.OutsideWindow, status.State);
        Assert.False(status.InWindow);
        Assert.Null(status.WindowEndsAtMinute);
        Assert.False(status.BrowsingAllowed);
    }

    // TIME-012: an adult without schedules or limits.
    [Fact]
    public void MemberWithoutSchedulesOrLimits_IsUnlimited()
    {
        var status = Evaluate(Dana, "2026-11-01T03:00", used: 500);

        Assert.Equal(new TimeStatus(TimeState.Unlimited, 500, null, null, null, true), status);
        Assert.True(status.BrowsingAllowed);
    }

    [Theory]
    [InlineData(0, TimeState.Ok, 60)]
    [InlineData(55, TimeState.Warn, 5)]
    [InlineData(60, TimeState.Expired, 0)]
    public void MemberWithoutSchedules_HasNoWindowButKeepsTheLimit(int used, TimeState state, int remaining)
    {
        var rules = new MemberTimeRules([], [new DailyLimit("m", Days(0, 1, 2, 3, 4, 5, 6), 60)]);

        var status = Evaluate(rules, "2026-11-01T02:00", used);

        Assert.Equal(new TimeStatus(state, used, 60, remaining, null, true), status);
    }

    [Fact]
    public void ScheduleWithoutLimitToday_IsUnlimitedInsideTheWindow()
    {
        var rules = Noa with { Limits = [new DailyLimit("noa", Days(5, 6), 180)] };

        var status = Evaluate(rules, "2026-11-01T16:00", used: 300);

        Assert.Equal(new TimeStatus(TimeState.Unlimited, 300, null, null, Minute("19:00"), true), status);
    }

    [Fact]
    public void Limit_AddsTodaysGrantsOnly()
    {
        TimeGrant[] grants =
        [
            new(new DateOnly(2026, 11, 1), 15),
            new(new DateOnly(2026, 11, 1), 30),
            new(new DateOnly(2026, 10, 31), 60),
            new(new DateOnly(2026, 11, 2), 60),
        ];

        var status = Evaluate(Noa, "2026-11-01T16:00", used: 100, grants);

        Assert.Equal(165, status.LimitMinutes);
        Assert.Equal(65, status.RemainingMinutes);
        Assert.Equal(TimeState.Ok, status.State);
    }

    [Fact]
    public void GrantsWithoutLimitToday_StayUnlimited()
    {
        var status = Evaluate(Dana, "2026-11-01T16:00", grants: [new(new DateOnly(2026, 11, 1), 30)]);

        Assert.Equal(TimeState.Unlimited, status.State);
        Assert.Null(status.LimitMinutes);
        Assert.Null(status.RemainingMinutes);
    }

    [Theory]
    [InlineData(0, TimeState.Ok, 120)]
    [InlineData(109, TimeState.Ok, 11)]
    [InlineData(110, TimeState.Warn, 10)]
    [InlineData(112, TimeState.Warn, 8)]
    [InlineData(119, TimeState.Warn, 1)]
    [InlineData(120, TimeState.Expired, 0)]
    public void State_FollowsRemainingMinutes(int used, TimeState state, int remaining)
    {
        var status = Evaluate(Noa, "2026-11-01T16:52", used);

        Assert.Equal(state, status.State);
        Assert.Equal(remaining, status.RemainingMinutes);
        Assert.Equal(120, status.LimitMinutes);
        Assert.Equal(Minute("19:00"), status.WindowEndsAtMinute);
    }

    // MD-004: the hub's union can exceed the limit after a disconnected agent reconnects.
    [Fact]
    public void Expired_WhenUsageExceedsTheLimit_ReportsZeroRemaining()
    {
        var status = Evaluate(Yoav with { Limits = [new DailyLimit("yoav", Days(1), 120)] }, "2026-11-02T16:30", used: 150);

        Assert.Equal(new TimeStatus(TimeState.Expired, 150, 120, 0, Minute("21:30"), true), status);
        Assert.False(status.BrowsingAllowed);
    }

    [Fact]
    public void ZeroLimit_IsExpiredAtOnce()
    {
        var status = Evaluate(Noa with { Limits = [new DailyLimit("noa", Days(0), 0)] }, "2026-11-01T16:00");

        Assert.Equal(TimeState.Expired, status.State);
        Assert.Equal(0, status.RemainingMinutes);
    }

    // TIME-008: outside the window wins over the limit.
    [Theory]
    [InlineData(30)]
    [InlineData(120)]
    public void OutsideWindow_TakesPrecedenceOverLimitStates(int used)
    {
        Assert.Equal(TimeState.OutsideWindow, Evaluate(Noa, "2026-11-01T19:30", used).State);
    }

    [Fact]
    public void AdjacentWindows_MergeIntoOne()
    {
        var rules = new MemberTimeRules(
            [
                new Schedule("m", Days(0), Minute("17:00"), Minute("19:00")),
                new Schedule("m", Days(0), Minute("15:00"), Minute("17:00")),
            ],
            []);

        Assert.Equal(Minute("19:00"), Evaluate(rules, "2026-11-01T15:30").WindowEndsAtMinute);
        Assert.Equal(Minute("19:00"), Evaluate(rules, "2026-11-01T17:00").WindowEndsAtMinute);
    }

    [Fact]
    public void OverlappingWindows_MergeIntoOne()
    {
        var rules = new MemberTimeRules(
            [
                new Schedule("m", Days(0), Minute("15:00"), Minute("18:00")),
                new Schedule("m", Days(0), Minute("16:00"), Minute("17:00")),
                new Schedule("m", Days(0), Minute("17:30"), Minute("19:00")),
                new Schedule("m", Days(0), Minute("20:00"), Minute("21:00")),
            ],
            []);

        Assert.Equal(Minute("19:00"), Evaluate(rules, "2026-11-01T16:30").WindowEndsAtMinute);
        Assert.False(Evaluate(rules, "2026-11-01T19:30").InWindow);
        Assert.Equal(Minute("21:00"), Evaluate(rules, "2026-11-01T20:00").WindowEndsAtMinute);
    }

    [Fact]
    public void WindowUntilMidnight_EndsAt1440()
    {
        var rules = new MemberTimeRules([new Schedule("m", Days(0), Minute("20:00"), 1440)], []);

        var status = Evaluate(rules, "2026-11-01T23:59:30");

        Assert.True(status.InWindow);
        Assert.Equal(1440, status.WindowEndsAtMinute);
        Assert.False(Evaluate(rules, "2026-11-02T00:00").InWindow);
    }

    [Fact]
    public void TemporaryWindow_AppliesOnlyOnItsDate()
    {
        TimeGrant[] grants = [new(new DateOnly(2026, 11, 2), 30, Minute("20:00"), Minute("20:30"))];

        Assert.True(Evaluate(Noa, "2026-11-02T20:10", grants: grants).InWindow);
        Assert.False(Evaluate(Noa, "2026-11-03T20:10", grants: grants).InWindow);
    }

    [Fact]
    public void TemporaryWindow_IsIgnoredForMemberWithoutSchedules()
    {
        TimeGrant[] grants = [new(new DateOnly(2026, 11, 2), 30, Minute("20:00"), Minute("20:30"))];

        var status = Evaluate(Dana, "2026-11-02T20:10", grants: grants);

        Assert.True(status.InWindow);
        Assert.Null(status.WindowEndsAtMinute);
    }

    [Fact]
    public void Evaluate_UsesTheFamilyZoneNotTheOffsetOfNow()
    {
        // 15:30 local on Sunday 2026-11-01 (UTC+2), expressed as New York time.
        var now = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.FromHours(-5));

        var status = Evaluate(Noa, now);

        Assert.True(status.InWindow);
        Assert.Equal(Minute("19:00"), status.WindowEndsAtMinute);
    }

    // TIME-003: 15:00-16:52 of browsing leaves 8 minutes.
    [Fact]
    public void LedgerAndCalculator_WarnAfter112Minutes()
    {
        var ledger = new UsageLedger();
        Browse(ledger, "noa", "pc-kids", "2026-11-01T15:00", "2026-11-01T16:52");
        var now = Local("2026-11-01T16:52");

        var status = Evaluate(Noa, now, Used(ledger, "noa", now));

        Assert.Equal(new TimeStatus(TimeState.Warn, 112, 120, 8, Minute("19:00"), true), status);
    }
}

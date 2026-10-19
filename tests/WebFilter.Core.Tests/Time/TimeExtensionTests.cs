using WebFilter.Core.Model;
using WebFilter.Core.Time;
using static WebFilter.Core.Tests.Time.TimeFixture;

namespace WebFilter.Core.Tests.Time;

public class TimeExtensionTests
{
    private static TimeGrant Extend(MemberTimeRules rules, string localNow, int minutes, params TimeGrant[] existing) =>
        TimeCalculator.CreateExtension(rules, Zone, Local(localNow), minutes, existing);

    // TIME-004: 2026-11-01 is a Sunday; the limit expired at 17:00 and the window ends at 19:00.
    [Fact]
    public void FarFromWindowEnd_OnlyRaisesTheLimit()
    {
        var grant = Extend(Noa, "2026-11-01T17:05", 15);

        Assert.Equal(new TimeGrant(new DateOnly(2026, 11, 1), 15), grant);

        var status = Evaluate(Noa, "2026-11-01T17:05", used: 120, grants: [grant]);
        Assert.Equal(new TimeStatus(TimeState.Ok, 120, 135, 15, Minute("19:00"), true), status);
    }

    [Fact]
    public void AfterExpiry_ExtraTimeIsUsedUpAndExpiresAgain()
    {
        var ledger = new UsageLedger();
        Browse(ledger, "noa", "pc-kids", "2026-11-01T15:00", "2026-11-01T17:00");
        var grant = Extend(Noa, "2026-11-01T17:05", 15);
        Browse(ledger, "noa", "pc-kids", "2026-11-01T17:05", "2026-11-01T17:20");
        var now = Local("2026-11-01T17:20");

        var status = Evaluate(Noa, now, Used(ledger, "noa", now), [grant]);

        Assert.Equal(new TimeStatus(TimeState.Expired, 135, 135, 0, Minute("19:00"), true), status);
    }

    // TIME-005: 2026-11-02 is a Monday; at 18:50 the window has 10 minutes left, fewer than the 15 granted.
    [Fact]
    public void NearWindowEnd_AddsTemporaryWindowThatExtendsTheCurrentOne()
    {
        var grant = Extend(Noa, "2026-11-02T18:50", 15);

        Assert.Equal(new TimeGrant(new DateOnly(2026, 11, 2), 15, Minute("18:50"), Minute("19:05")), grant);

        var status = Evaluate(Noa, "2026-11-02T18:50", used: 60, grants: [grant]);
        Assert.Equal(new TimeStatus(TimeState.Ok, 60, 135, 75, Minute("19:05"), true), status);
        Assert.True(Evaluate(Noa, "2026-11-02T19:04", used: 60, grants: [grant]).InWindow);
        Assert.False(Evaluate(Noa, "2026-11-02T19:05", used: 60, grants: [grant]).InWindow);
    }

    [Fact]
    public void ExactlyNMinutesLeft_NeedsNoTemporaryWindow()
    {
        var grant = Extend(Noa, "2026-11-02T18:45", 15);

        Assert.Null(grant.TempWindowStart);
        Assert.Null(grant.TempWindowEnd);
    }

    [Fact]
    public void NearWindowEnd_UsesTheFlooredMinute()
    {
        var grant = TimeCalculator.CreateExtension(Noa, Zone, Local("2026-11-02T18:50:40"), 15, []);

        Assert.Equal(Minute("18:50"), grant.TempWindowStart);
        Assert.Equal(Minute("19:05"), grant.TempWindowEnd);
    }

    // TIME-006: Monday 20:00 is after noa's 15:00-19:00 window.
    [Fact]
    public void OutsideAnyWindow_OpensTemporaryWindowStartingNow()
    {
        var grant = Extend(Noa, "2026-11-02T20:00", 30);

        Assert.Equal(new TimeGrant(new DateOnly(2026, 11, 2), 30, Minute("20:00"), Minute("20:30")), grant);

        var status = Evaluate(Noa, "2026-11-02T20:00", grants: [grant]);
        Assert.Equal(new TimeStatus(TimeState.Ok, 0, 150, 150, Minute("20:30"), true), status);
        Assert.True(Evaluate(Noa, "2026-11-02T20:10", grants: [grant]).InWindow);
        Assert.False(Evaluate(Noa, "2026-11-02T20:30", grants: [grant]).InWindow);
    }

    [Fact]
    public void OnDayWithoutWindows_OpensTemporaryWindow()
    {
        var rules = Yoav with { Schedules = [new Schedule("yoav", Days(0, 1, 2, 3, 4), Minute("14:00"), Minute("21:30"))] };

        var grant = Extend(rules, "2026-11-06T10:00", 30);

        Assert.Equal(new TimeGrant(new DateOnly(2026, 11, 6), 30, Minute("10:00"), Minute("10:30")), grant);
        Assert.Equal(Minute("10:30"), Evaluate(rules, "2026-11-06T10:15", grants: [grant]).WindowEndsAtMinute);
    }

    [Fact]
    public void TemporaryWindow_IsCappedAtMidnight()
    {
        // Saturday 2026-11-07: yoav's window ends at 23:00.
        var grant = Extend(Yoav, "2026-11-07T23:50", 30);

        Assert.Equal(new TimeGrant(new DateOnly(2026, 11, 7), 30, Minute("23:50"), 1440), grant);
        Assert.Equal(1440, Evaluate(Yoav, "2026-11-07T23:59", grants: [grant]).WindowEndsAtMinute);
    }

    [Fact]
    public void MemberWithoutSchedules_GetsNoTemporaryWindow()
    {
        var rules = new MemberTimeRules([], [new DailyLimit("m", Days(1), 60)]);

        var grant = Extend(rules, "2026-11-02T20:00", 30);

        Assert.Equal(new TimeGrant(new DateOnly(2026, 11, 2), 30), grant);
    }

    [Fact]
    public void ExistingTemporaryWindows_CountAsPartOfTheCurrentWindow()
    {
        var first = Extend(Noa, "2026-11-02T18:50", 15);

        // The merged window now ends at 19:05: 13 minutes left at 18:52 is enough for 10 more.
        Assert.Null(Extend(Noa, "2026-11-02T18:52", 10, first).TempWindowStart);

        // At 19:00 only 5 minutes are left, so another 15 need a new temporary window.
        var second = Extend(Noa, "2026-11-02T19:00", 15, first);
        Assert.Equal(new TimeGrant(new DateOnly(2026, 11, 2), 15, Minute("19:00"), Minute("19:15")), second);

        var status = Evaluate(Noa, "2026-11-02T19:00", used: 60, grants: [first, second]);
        Assert.Equal(Minute("19:15"), status.WindowEndsAtMinute);
        Assert.Equal(150, status.LimitMinutes);
    }

    [Fact]
    public void GrantsOfOtherDays_AreIgnored()
    {
        var yesterday = new TimeGrant(new DateOnly(2026, 11, 1), 60, Minute("19:00"), Minute("20:00"));

        var grant = Extend(Noa, "2026-11-02T19:10", 15, yesterday);

        Assert.Equal(Minute("19:10"), grant.TempWindowStart);
    }

    [Fact]
    public void GrantDate_IsTheLocalDate()
    {
        // 01:00 local on Monday 2026-11-02 is still Sunday in UTC.
        var now = Utc(2026, 11, 1, 23, 0);

        var grant = TimeCalculator.CreateExtension(Noa, Zone, now, 30, []);

        Assert.Equal(new TimeGrant(new DateOnly(2026, 11, 2), 30, 60, 90), grant);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-15)]
    public void NonPositiveMinutes_AreRejected(int minutes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Extend(Noa, "2026-11-02T18:50", minutes));
    }
}

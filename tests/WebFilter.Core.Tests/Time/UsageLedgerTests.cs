using WebFilter.Core.Time;
using static WebFilter.Core.Tests.Time.TimeFixture;

namespace WebFilter.Core.Tests.Time;

public class UsageLedgerTests
{
    private static readonly DateOnly Sunday = new(2026, 11, 1);
    private static readonly DateOnly Monday = new(2026, 11, 2);

    // README §6: browsing 15:00-17:00 = 120 minutes, credited by 17:00.
    [Theory]
    [InlineData("2026-11-01T15:00:00", 0)]
    [InlineData("2026-11-01T15:00:59", 0)]
    [InlineData("2026-11-01T15:01:00", 1)]
    [InlineData("2026-11-01T16:59:59", 119)]
    [InlineData("2026-11-01T17:00:00", 120)]
    [InlineData("2026-11-01T20:00:00", 120)]
    public void Minute_IsCreditedAtItsEnd(string asOf, int expected)
    {
        var ledger = new UsageLedger();
        Browse(ledger, "noa", "pc-kids", "2026-11-01T15:00", "2026-11-01T17:00"); // last heartbeat 16:59:30

        Assert.Equal(expected, ledger.CreditedMinutes("noa", Sunday, Zone, Local(asOf)));
    }

    [Fact]
    public void Mark_IsIdempotentWithinAMinute()
    {
        var ledger = new UsageLedger();
        ledger.Mark("noa", "pc-kids", Local("2026-11-01T15:00:05"));
        ledger.Mark("noa", "pc-kids", Local("2026-11-01T15:00:05"));
        ledger.Mark("noa", "pc-kids", Local("2026-11-01T15:00:59").AddMilliseconds(999));

        var minute = Assert.Single(ledger.MinutesOn("noa", "pc-kids"));
        Assert.Equal(Utc(2026, 11, 1, 13, 0), minute);
        Assert.Equal(TimeSpan.Zero, minute.Offset);
        Assert.Equal(1, ledger.CreditedMinutes("noa", Sunday, Zone, Local("2026-11-01T15:01")));
    }

    [Fact]
    public void Mark_KeysByTheInstantNotItsOffset()
    {
        var ledger = new UsageLedger();
        ledger.Mark("noa", "pc-kids", new DateTimeOffset(2026, 11, 1, 15, 0, 10, TimeSpan.FromHours(2)));
        ledger.Mark("noa", "pc-kids", new DateTimeOffset(2026, 11, 1, 8, 0, 40, TimeSpan.FromHours(-5)));

        Assert.Single(ledger.MinutesOn("noa", "pc-kids"));
    }

    // MD-001: 2026-11-02 is a Monday.
    [Fact]
    public void OverlappingMinutesOnTwoDevices_CountOnce()
    {
        var ledger = new UsageLedger();
        Browse(ledger, "yoav", "pc-kids", "2026-11-02T15:00", "2026-11-02T16:00");
        Browse(ledger, "yoav", "laptop-yoav", "2026-11-02T15:30", "2026-11-02T16:30");

        Assert.Equal(90, ledger.CreditedMinutes("yoav", Monday, Zone, Local("2026-11-02T16:30")));
        Assert.Equal(60, ledger.MinutesOn("yoav", "pc-kids").Count);
        Assert.Equal(60, ledger.MinutesOn("yoav", "laptop-yoav").Count);
    }

    // MD-002: two browsers on one device report the same device id.
    [Fact]
    public void TwoBrowsersOnOneDevice_CountOnce()
    {
        var ledger = new UsageLedger();
        Browse(ledger, "yoav", "pc-kids", "2026-11-02T15:00", "2026-11-02T15:40");
        Browse(ledger, "yoav", "pc-kids", Local("2026-11-02T15:20").AddSeconds(10), Local("2026-11-02T16:00"));

        Assert.Equal(60, ledger.CreditedMinutes("yoav", Monday, Zone, Local("2026-11-02T16:00")));
    }

    [Fact]
    public void Members_AreCountedSeparately()
    {
        var ledger = new UsageLedger();
        Browse(ledger, "noa", "pc-kids", "2026-11-01T15:00", "2026-11-01T15:30");
        Browse(ledger, "yoav", "pc-kids", "2026-11-01T15:00", "2026-11-01T16:00");

        var asOf = Local("2026-11-01T17:00");
        Assert.Equal(30, ledger.CreditedMinutes("noa", Sunday, Zone, asOf));
        Assert.Equal(60, ledger.CreditedMinutes("yoav", Sunday, Zone, asOf));
        Assert.Equal(0, ledger.CreditedMinutes("dana", Sunday, Zone, asOf));
    }

    [Fact]
    public void Minutes_AreAssignedToTheirLocalDate()
    {
        var ledger = new UsageLedger();
        // 23:58-00:02 local is 21:58-22:02 UTC, all on the same UTC date.
        Browse(ledger, "noa", "pc-kids", "2026-11-01T23:58", "2026-11-02T00:02");

        var asOf = Local("2026-11-02T00:02");
        Assert.Equal(2, ledger.CreditedMinutes("noa", Sunday, Zone, asOf));
        Assert.Equal(2, ledger.CreditedMinutes("noa", Monday, Zone, asOf));
        Assert.Equal(0, ledger.CreditedMinutes("noa", new DateOnly(2026, 11, 3), Zone, asOf));
    }

    [Fact]
    public void TheDstNight_Has25HoursOfMinutes()
    {
        var ledger = new UsageLedger();
        // 00:00+03:00 to 03:00+02:00 on 2026-10-25: three wall-clock hours, four real ones.
        Browse(ledger, "yoav", "pc-kids", Utc(2026, 10, 24, 21, 0), Utc(2026, 10, 25, 1, 0));

        var asOf = Utc(2026, 10, 25, 1, 0);
        Assert.Equal(240, ledger.CreditedMinutes("yoav", new DateOnly(2026, 10, 25), Zone, asOf));
        Assert.Equal(0, ledger.CreditedMinutes("yoav", new DateOnly(2026, 10, 24), Zone, asOf));
    }

    [Fact]
    public void MinutesOn_ReturnsOneDevicesMinutesInOrder()
    {
        var ledger = new UsageLedger();
        ledger.Mark("yoav", "laptop-yoav", Local("2026-11-02T15:02:30"));
        ledger.Mark("yoav", "laptop-yoav", Local("2026-11-02T15:00:30"));
        ledger.Mark("yoav", "pc-kids", Local("2026-11-02T15:01:30"));

        DateTimeOffset[] expected = [Utc(2026, 11, 2, 13, 0), Utc(2026, 11, 2, 13, 2)];
        Assert.Equal(expected, ledger.MinutesOn("yoav", "laptop-yoav"));
        Assert.Empty(ledger.MinutesOn("yoav", "unknown-device"));
        Assert.Empty(ledger.MinutesOn("unknown-member", "pc-kids"));
    }

    [Fact]
    public void MinutesOn_IsASnapshot()
    {
        var ledger = new UsageLedger();
        ledger.Mark("noa", "pc-kids", Local("2026-11-01T15:00"));
        var snapshot = ledger.MinutesOn("noa", "pc-kids");

        ledger.Mark("noa", "pc-kids", Local("2026-11-01T15:01"));

        Assert.Single(snapshot);
    }

    // MD-004: the laptop browsed offline; the hub only knows pc-kids until the laptop reconnects.
    [Fact]
    public void MergeFrom_UnitesTheLedgers()
    {
        var hub = new UsageLedger();
        var laptop = new UsageLedger();
        Browse(hub, "yoav", "pc-kids", "2026-11-02T14:00", "2026-11-02T15:00");
        Browse(laptop, "yoav", "laptop-yoav", "2026-11-02T15:00", "2026-11-02T16:30");
        var asOf = Local("2026-11-02T16:30");
        Assert.Equal(60, hub.CreditedMinutes("yoav", Monday, Zone, asOf));
        Assert.Equal(90, laptop.CreditedMinutes("yoav", Monday, Zone, asOf));

        hub.MergeFrom(laptop);
        hub.MergeFrom(laptop);

        Assert.Equal(150, hub.CreditedMinutes("yoav", Monday, Zone, asOf));
        Assert.Equal(90, laptop.CreditedMinutes("yoav", Monday, Zone, asOf));
        Assert.Equal(laptop.MinutesOn("yoav", "laptop-yoav"), hub.MinutesOn("yoav", "laptop-yoav"));
    }

    [Fact]
    public void MergeFrom_OverlappingMinutes_CountOnce()
    {
        var a = new UsageLedger();
        var b = new UsageLedger();
        Browse(a, "yoav", "pc-kids", "2026-11-02T15:00", "2026-11-02T16:00");
        Browse(b, "yoav", "pc-kids", "2026-11-02T15:30", "2026-11-02T16:30");

        a.MergeFrom(b);

        Assert.Equal(90, a.MinutesOn("yoav", "pc-kids").Count);
    }

    [Fact]
    public void MergeFrom_DoesNotShareStateWithTheSource()
    {
        var hub = new UsageLedger();
        var laptop = new UsageLedger();
        laptop.Mark("yoav", "laptop-yoav", Local("2026-11-02T15:00"));
        hub.MergeFrom(laptop);

        laptop.Mark("yoav", "laptop-yoav", Local("2026-11-02T15:01"));

        Assert.Single(hub.MinutesOn("yoav", "laptop-yoav"));
    }

    [Fact]
    public void MergeFrom_Itself_ChangesNothing()
    {
        var ledger = new UsageLedger();
        Browse(ledger, "noa", "pc-kids", "2026-11-01T15:00", "2026-11-01T15:10");

        ledger.MergeFrom(ledger);

        Assert.Equal(10, ledger.MinutesOn("noa", "pc-kids").Count);
    }

    [Fact]
    public void Clone_IsAnIndependentCopy()
    {
        var original = new UsageLedger();
        Browse(original, "noa", "pc-kids", "2026-11-01T15:00", "2026-11-01T15:30");
        Browse(original, "yoav", "laptop-yoav", "2026-11-01T15:00", "2026-11-01T15:10");
        var asOf = Local("2026-11-01T18:00");

        var clone = original.Clone();
        Assert.Equal(30, clone.CreditedMinutes("noa", Sunday, Zone, asOf));
        Assert.Equal(10, clone.CreditedMinutes("yoav", Sunday, Zone, asOf));

        clone.Mark("noa", "pc-kids", Local("2026-11-01T16:00"));
        original.Mark("yoav", "laptop-yoav", Local("2026-11-01T16:00"));

        Assert.Equal(31, clone.CreditedMinutes("noa", Sunday, Zone, asOf));
        Assert.Equal(30, original.CreditedMinutes("noa", Sunday, Zone, asOf));
        Assert.Equal(10, clone.CreditedMinutes("yoav", Sunday, Zone, asOf));
        Assert.Equal(11, original.CreditedMinutes("yoav", Sunday, Zone, asOf));
    }
}

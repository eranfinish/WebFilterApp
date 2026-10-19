using System.Globalization;
using WebFilter.Core.Model;
using WebFilter.Core.Time;

namespace WebFilter.Core.Tests.Time;

/// <summary>The time rules of tests/simulation/fixtures/family-base.json and helpers for wall-clock instants.</summary>
internal static class TimeFixture
{
    public static readonly TimeZoneInfo Zone = FindZone();

    public static MemberTimeRules Noa { get; } = new(
        [
            new Schedule("noa", Days(0, 1, 2, 3, 4), Minute("15:00"), Minute("19:00")),
            new Schedule("noa", Days(5), Minute("09:00"), Minute("13:00")),
            new Schedule("noa", Days(5), Minute("16:00"), Minute("20:00")),
            new Schedule("noa", Days(6), Minute("09:00"), Minute("20:00")),
        ],
        [
            new DailyLimit("noa", Days(0, 1, 2, 3, 4), 120),
            new DailyLimit("noa", Days(5, 6), 180),
        ]);

    public static MemberTimeRules Yoav { get; } = new(
        [
            new Schedule("yoav", Days(0, 1, 2, 3, 4), Minute("14:00"), Minute("21:30")),
            new Schedule("yoav", Days(5, 6), Minute("09:00"), Minute("23:00")),
        ],
        [
            new DailyLimit("yoav", Days(0, 1, 2, 3, 4), 240),
            new DailyLimit("yoav", Days(5, 6), 300),
        ]);

    /// <summary>An unrestricted adult: no schedules, no limits.</summary>
    public static MemberTimeRules Dana { get; } = new([], []);

    /// <summary>Days as in the fixture JSON (0 = Sunday).</summary>
    public static HashSet<DayOfWeek> Days(params int[] days) => days.Select(d => (DayOfWeek)d).ToHashSet();

    public static int Minute(string hhmm)
    {
        var time = TimeOnly.ParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture);
        return time.Hour * 60 + time.Minute;
    }

    /// <summary>A wall-clock time in the family zone, <c>yyyy-MM-ddTHH:mm[:ss]</c>. Must be neither skipped nor repeated.</summary>
    public static DateTimeOffset Local(string wallClock)
    {
        var dt = DateTime.ParseExact(wallClock, ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None);
        Assert.False(Zone.IsAmbiguousTime(dt) || Zone.IsInvalidTime(dt), $"{wallClock} is not a unique local time");
        return new DateTimeOffset(dt, Zone.GetUtcOffset(dt));
    }

    public static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    public static TimeStatus Evaluate(MemberTimeRules rules, DateTimeOffset now, int used = 0, IReadOnlyList<TimeGrant>? grants = null) =>
        TimeCalculator.Evaluate(new TimeQuery(rules, Zone, now, used, grants ?? []));

    public static TimeStatus Evaluate(MemberTimeRules rules, string localNow, int used = 0, IReadOnlyList<TimeGrant>? grants = null) =>
        Evaluate(rules, Local(localNow), used, grants);

    /// <summary>A browse session: one heartbeat every 30 seconds from <paramref name="from"/> until before <paramref name="to"/>.</summary>
    public static void Browse(UsageLedger ledger, string memberId, string deviceId, DateTimeOffset from, DateTimeOffset to)
    {
        for (var t = from; t < to; t = t.AddSeconds(30))
            ledger.Mark(memberId, deviceId, t);
    }

    public static void Browse(UsageLedger ledger, string memberId, string deviceId, string localFrom, string localTo) =>
        Browse(ledger, memberId, deviceId, Local(localFrom), Local(localTo));

    /// <summary>Credited minutes for the local date of <paramref name="asOf"/>.</summary>
    public static int Used(UsageLedger ledger, string memberId, DateTimeOffset asOf) =>
        ledger.CreditedMinutes(memberId, TimeCalculator.LocalDate(asOf, Zone), Zone, asOf);

    private static TimeZoneInfo FindZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");
        }
    }
}

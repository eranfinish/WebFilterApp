namespace WebFilter.Admin.Services;

/// <summary>
/// Wall-clock helpers in the family's time zone (<c>FamilyBundle.TimeZone</c>, an IANA id), which is what the service
/// uses for days and windows — not necessarily this PC's zone.
/// </summary>
public static class FamilyTime
{
    /// <summary>The zone for an IANA (or Windows) id; this PC's zone when the id is missing or unknown.</summary>
    public static TimeZoneInfo FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Local;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local;
        }
    }

    public static DateTimeOffset ToLocal(DateTimeOffset instant, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(instant, zone);

    /// <summary>The next local midnight after <paramref name="now"/> ("until the end of the day").</summary>
    public static DateTimeOffset EndOfDay(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var midnight = ToLocal(now, zone).Date.AddDays(1);
        // A DST jump at midnight skips it; the day then ends at the first valid local time.
        while (zone.IsInvalidTime(midnight)) midnight = midnight.AddMinutes(15);
        var offset = zone.IsAmbiguousTime(midnight) ? zone.GetAmbiguousTimeOffsets(midnight).Max() : zone.GetUtcOffset(midnight);
        return new DateTimeOffset(midnight, offset);
    }
}

namespace WebFilter.Core.Time;

/// <summary>
/// Which minutes each member spent browsing, per device. A minute is identified by its UTC start (seconds = 0).
/// Marking is idempotent; several devices or browsers in the same minute count once (union by minute).
/// A minute is credited at its end: it counts towards usage at instant t only if minuteStart + 1 min ≤ t.
/// The engine marks a minute only when browsing was allowed at the heartbeat — the ledger itself has no rules.
/// </summary>
public sealed class UsageLedger
{
    // member → device → marked minutes, each stored as whole minutes since 0001-01-01 UTC.
    private readonly Dictionary<string, Dictionary<string, SortedSet<long>>> _minutes = new(StringComparer.Ordinal);

    /// <summary>Marks the minute containing <paramref name="instant"/> for the member on the device.</summary>
    public void Mark(string memberId, string deviceId, DateTimeOffset instant) =>
        MinutesOf(memberId, deviceId).Add(FloorMinutes(instant));

    /// <summary>Distinct minutes (any device) whose local date is <paramref name="localDate"/> and that have ended by <paramref name="asOf"/>.</summary>
    public int CreditedMinutes(string memberId, DateOnly localDate, TimeZoneInfo zone, DateTimeOffset asOf)
    {
        if (!_minutes.TryGetValue(memberId, out var devices))
            return 0;

        var dayStart = CeilingMinutes(TimeCalculator.StartOfLocalDay(localDate, zone));
        var dayEnd = CeilingMinutes(TimeCalculator.StartOfLocalDay(localDate.AddDays(1), zone));
        // Minute m has ended by asOf ⇔ m + 1 ≤ asOf ⇔ m < floor(asOf).
        var end = Math.Min(dayEnd, FloorMinutes(asOf));
        if (dayStart >= end)
            return 0;

        var credited = new HashSet<long>();
        foreach (var minutes in devices.Values)
            credited.UnionWith(minutes.GetViewBetween(dayStart, end - 1));
        return credited.Count;
    }

    /// <summary>All marked minute starts of a member on one device (for syncing an agent's local usage to the hub).</summary>
    public IReadOnlyCollection<DateTimeOffset> MinutesOn(string memberId, string deviceId) =>
        _minutes.TryGetValue(memberId, out var devices) && devices.TryGetValue(deviceId, out var minutes)
            ? minutes.Select(m => new DateTimeOffset(m * TimeSpan.TicksPerMinute, TimeSpan.Zero)).ToArray()
            : [];

    /// <summary>Every marked minute (for persistence).</summary>
    public IEnumerable<(string MemberId, string DeviceId, DateTimeOffset Minute)> Entries() =>
        from member in _minutes
        from device in member.Value
        from minute in device.Value
        select (member.Key, device.Key, new DateTimeOffset(minute * TimeSpan.TicksPerMinute, TimeSpan.Zero));

    /// <summary>Adds every entry of <paramref name="other"/> to this ledger.</summary>
    public void MergeFrom(UsageLedger other)
    {
        if (ReferenceEquals(other, this))
            return;
        foreach (var (memberId, devices) in other._minutes)
        {
            foreach (var (deviceId, minutes) in devices)
                MinutesOf(memberId, deviceId).UnionWith(minutes);
        }
    }

    public UsageLedger Clone()
    {
        var clone = new UsageLedger();
        clone.MergeFrom(this);
        return clone;
    }

    private SortedSet<long> MinutesOf(string memberId, string deviceId)
    {
        if (!_minutes.TryGetValue(memberId, out var devices))
            _minutes[memberId] = devices = new Dictionary<string, SortedSet<long>>(StringComparer.Ordinal);
        if (!devices.TryGetValue(deviceId, out var minutes))
            devices[deviceId] = minutes = [];
        return minutes;
    }

    private static long FloorMinutes(DateTimeOffset instant) => instant.UtcTicks / TimeSpan.TicksPerMinute;

    private static long CeilingMinutes(DateTimeOffset instant) =>
        (instant.UtcTicks + TimeSpan.TicksPerMinute - 1) / TimeSpan.TicksPerMinute;
}

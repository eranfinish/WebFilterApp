namespace WebFilter.Core.Time;

/// <summary>
/// Which minutes each member spent browsing, per device. A minute is identified by its UTC start (seconds = 0).
/// Marking is idempotent; several devices or browsers in the same minute count once (union by minute).
/// A minute is credited at its end: it counts towards usage at instant t only if minuteStart + 1 min ≤ t.
/// The engine marks a minute only when browsing was allowed at the heartbeat — the ledger itself has no rules.
/// </summary>
public sealed class UsageLedger
{
    /// <summary>Marks the minute containing <paramref name="instant"/> for the member on the device.</summary>
    public void Mark(string memberId, string deviceId, DateTimeOffset instant) => throw new NotImplementedException();

    /// <summary>Distinct minutes (any device) whose local date is <paramref name="localDate"/> and that have ended by <paramref name="asOf"/>.</summary>
    public int CreditedMinutes(string memberId, DateOnly localDate, TimeZoneInfo zone, DateTimeOffset asOf) => throw new NotImplementedException();

    /// <summary>All marked minute starts of a member on one device (for syncing an agent's local usage to the hub).</summary>
    public IReadOnlyCollection<DateTimeOffset> MinutesOn(string memberId, string deviceId) => throw new NotImplementedException();

    /// <summary>Adds every entry of <paramref name="other"/> to this ledger.</summary>
    public void MergeFrom(UsageLedger other) => throw new NotImplementedException();

    public UsageLedger Clone() => throw new NotImplementedException();
}

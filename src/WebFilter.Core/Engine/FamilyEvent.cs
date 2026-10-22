using WebFilter.Core.Decisions;

namespace WebFilter.Core.Engine;

/// <summary>
/// Event types. Wire names (sync and admin protocols): time_warning, time_expired, blocked, app_blocked, admin_change,
/// access_request, tamper, service, search (a search the member made, when the member's searches are logged —
/// <see cref="FamilyEvent.Detail"/> "engine:query", <see cref="FamilyEvent.Url"/> the results page), override (a teen chose
/// "open anyway" on a jump block — <see cref="FamilyEvent.Url"/> the site opened, reason blocked_jump).
/// </summary>
public enum EventType { TimeWarning, TimeExpired, Blocked, AppBlocked, AdminChange, AccessRequest, Tamper, Service, Search, Override }

/// <summary>Something worth showing a parent. Raised on an agent or the hub; agents forward theirs to the hub.</summary>
public sealed record FamilyEvent(
    EventType Type,
    DateTimeOffset At,
    string? MemberId,
    string? DeviceId,
    ReasonCode Reason = ReasonCode.None,
    string? Url = null,
    string? App = null,
    int? RemainingMinutes = null,
    string? Detail = null,
    string? Admin = null);

/// <summary>
/// The <see cref="FamilyEvent.Detail"/> of <see cref="EventType.Tamper"/> events (PROTECTION-SPEC §10). Each becomes a
/// parent notification of kind "tamper" on the hub, with the same detail.
/// </summary>
public static class TamperKinds
{
    /// <summary>Browser or Windows policies this service manages had been changed and were restored.</summary>
    public const string PolicyRepaired = "policy_repaired";

    /// <summary>The network filters of an account had been removed and were restored.</summary>
    public const string NetworkRepaired = "network_repaired";

    /// <summary>At start, the service found that it had not stopped cleanly the previous time (killed, crashed, power cut).</summary>
    public const string UncleanStop = "unclean_stop";
}

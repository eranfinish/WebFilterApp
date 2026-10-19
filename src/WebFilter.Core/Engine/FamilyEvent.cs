using WebFilter.Core.Decisions;

namespace WebFilter.Core.Engine;

public enum EventType { TimeWarning, TimeExpired, Blocked, AppBlocked, AdminChange, AccessRequest, Tamper, Service }

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
    string? Detail = null);

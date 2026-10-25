using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Decisions;
using WebFilter.Core.Engine;
using WebFilter.Core.Time;

namespace WebFilter.Service.Mapping;

/// <summary>Engine snapshots and uploads ⇄ the hub API's platform-neutral DTOs.</summary>
public static class SyncMapper
{
    /// <summary>Only recent usage travels: enough for today in any time zone plus a margin.</summary>
    public static readonly TimeSpan UsageWindow = TimeSpan.FromHours(50);

    public static SnapshotDto ToDto(HubSnapshot snapshot, DateTimeOffset now) => new(
        snapshot.Version,
        BundleMapper.ToBundle(snapshot.Config, snapshot.Version),
        snapshot.Runtime.ToDictionary(kv => kv.Key, kv => new RuntimeDto(kv.Value.PausedUntil, kv.Value.HomeworkUntil,
            kv.Value.Grants.Select(g => new GrantDto(g.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g.ExtraMinutes, g.TempWindowStart, g.TempWindowEnd)).ToList())),
        Usage(snapshot.Usage, now),
        snapshot.Decisions?.Select(d => new DecisionDto(d.RequestId, d.MemberId, d.DeviceId, d.Kind.ToString().ToLowerInvariant(), d.TargetKey,
            d.Status.ToString().ToLowerInvariant(), d.DecidedAt, d.Until)).ToList());

    /// <summary>A snapshot as an agent applies it. A decision this version cannot read (a newer hub's kind or status) is left out.</summary>
    public static HubSnapshot FromDto(SnapshotDto dto) => new(
        dto.Version,
        BundleMapper.ToConfig(dto.Settings),
        dto.Runtime.ToDictionary(kv => kv.Key, kv => new MemberRuntime
        {
            PausedUntil = kv.Value.PausedUntil,
            HomeworkUntil = kv.Value.HomeworkUntil,
            Grants = kv.Value.Grants.Select(g => new TimeGrant(DateOnly.ParseExact(g.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), g.ExtraMinutes, g.TempWindowStart, g.TempWindowEnd)).ToList(),
        }),
        Ledger(dto.Usage),
        dto.Decisions?.Select(FromDto).OfType<RequestDecision>().ToList());

    private static RequestDecision? FromDto(DecisionDto d)
    {
        RequestKind? kind = d.Kind switch { "url" => RequestKind.Url, "youtube" => RequestKind.YouTube, "app" => RequestKind.App, "time" => RequestKind.Time, _ => null };
        RequestStatus? status = d.Status switch { "approved" => RequestStatus.Approved, "denied" => RequestStatus.Denied, _ => null };
        return kind is { } k && status is { } s ? new RequestDecision(d.Id, d.Member, d.Device, k, d.TargetKey, s, d.DecidedAt, d.Until) : null;
    }

    public static UploadDto ToDto(AgentUpload upload, DateTimeOffset now) => new(
        upload.DeviceId,
        Usage(upload.Usage, now),
        upload.Events.Select(e => new UploadEventDto(EventId(e), ToDto(e))).ToList(),
        upload.QueuedRequests.Select(ToDto).ToList());

    /// <summary>The agent's upload as the hub ingests it; events already received (by id) are dropped by <paramref name="isNew"/>.</summary>
    public static AgentUpload FromDto(UploadDto dto, Func<string, bool> isNew) => new(
        dto.DeviceId,
        Ledger(dto.Usage.Where(u => u.Device == dto.DeviceId)),
        dto.Events.Where(e => isNew(e.EventId)).Select(e => FromDto(e.Event, dto.DeviceId)).ToList(),
        dto.QueuedRequests.Select(r => FromDto(r, dto.DeviceId)).ToList());

    public static RequestDto ToDto(AccessRequest r) => new(r.Id, r.MemberId, r.DeviceId, r.Kind.ToString().ToLowerInvariant(), r.Target, r.TargetKey,
        r.Minutes, r.Message, r.CreatedAt, r.Status.ToString().ToLowerInvariant(), r.DecidedAt, r.Title, r.Handle, r.SearchQuery);

    /// <summary>
    /// A request as received from an agent; the device id always comes from the authenticated certificate. A YouTube
    /// request's channel names are cleaned again (<see cref="RequestNames"/>); other kinds carry none.
    /// </summary>
    public static AccessRequest FromDto(RequestDto r, string deviceId)
    {
        var kind = Enum.Parse<RequestKind>(r.Kind, true);
        var youtube = kind == RequestKind.YouTube;
        return new()
        {
            Id = r.Id, MemberId = r.MemberId, DeviceId = deviceId, Kind = kind, Target = r.Target, TargetKey = r.TargetKey,
            Minutes = r.Minutes, Message = r.Message, CreatedAt = r.CreatedAt,
            Title = youtube ? RequestNames.Title(r.Title) : null,
            Handle = youtube ? RequestNames.Handle(r.Handle) : null,
            SearchQuery = youtube ? RequestNames.SearchQuery(r.SearchQuery) : null,
        };
    }

    public static EventDto ToDto(FamilyEvent e) => new(EventWire(e.Type), e.At, e.MemberId, e.DeviceId,
        e.Reason == ReasonCode.None ? null : e.Reason.ToWire(), e.Url, e.App, e.RemainingMinutes, e.Detail);

    /// <summary>An event an agent uploaded. A reason code this version does not know (a newer agent) is dropped, not refused.</summary>
    public static FamilyEvent FromDto(EventDto e, string deviceId) => new(ParseEvent(e.Type), e.At, e.MemberId, deviceId,
        ReasonCodeExtensions.ParseOrNone(e.Reason), e.Url, e.App, e.RemainingMinutes, e.Detail);

    /// <summary>Stable id of an agent event, so a retried upload does not duplicate it.</summary>
    public static string EventId(FamilyEvent e) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{e.Type}|{e.At.UtcTicks}|{e.MemberId}|{e.DeviceId}|{e.Reason}|{e.Url}|{e.App}|{e.RemainingMinutes}|{e.Detail}")))[..32];

    /// <summary>An event type's wire name (sync and admin protocols; see <see cref="EventType"/>).</summary>
    public static string EventWire(EventType type) => type switch
    {
        EventType.TimeWarning => "time_warning", EventType.TimeExpired => "time_expired", EventType.Blocked => "blocked",
        EventType.AppBlocked => "app_blocked", EventType.AdminChange => "admin_change", EventType.AccessRequest => "access_request",
        EventType.Tamper => "tamper", EventType.Search => "search", EventType.Override => "override", _ => "service",
    };

    /// <summary>An event type from its wire name; a name this version does not know (a newer agent) is a service event.</summary>
    public static EventType ParseEvent(string wire) => wire switch
    {
        "time_warning" => EventType.TimeWarning, "time_expired" => EventType.TimeExpired, "blocked" => EventType.Blocked,
        "app_blocked" => EventType.AppBlocked, "admin_change" => EventType.AdminChange, "access_request" => EventType.AccessRequest,
        "tamper" => EventType.Tamper, "search" => EventType.Search, "override" => EventType.Override, _ => EventType.Service,
    };

    private static List<UsageDto> Usage(UsageLedger ledger, DateTimeOffset now)
    {
        var cutoff = now - UsageWindow;
        return ledger.Entries()
            .Where(e => e.Minute >= cutoff)
            .GroupBy(e => (e.MemberId, e.DeviceId))
            .Select(g => new UsageDto(g.Key.MemberId, g.Key.DeviceId, g.Select(e => e.Minute.ToUnixTimeSeconds() / 60).Order().ToList()))
            .ToList();
    }

    private static UsageLedger Ledger(IEnumerable<UsageDto> usage)
    {
        var ledger = new UsageLedger();
        foreach (var u in usage)
            foreach (var minute in u.Minutes)
                ledger.Mark(u.Member, u.Device, DateTimeOffset.FromUnixTimeSeconds(minute * 60));
        return ledger;
    }
}

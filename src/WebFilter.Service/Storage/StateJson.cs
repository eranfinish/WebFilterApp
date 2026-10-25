using System.Globalization;
using System.Text.Json;
using WebFilter.Core.Decisions;
using WebFilter.Core.Engine;
using WebFilter.Core.Time;

namespace WebFilter.Service.Storage;

/// <summary>Storage representations of engine state that has no wire contract of its own.</summary>
internal static class StateJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private sealed record GrantDto(string LocalDate, int ExtraMinutes, int? TempWindowStart, int? TempWindowEnd);

    private sealed record EventDto(string Type, DateTimeOffset At, string? MemberId, string? DeviceId, string? Reason, string? Url, string? App, int? RemainingMinutes, string? Detail);

    private sealed record RequestDto(string Id, string MemberId, string DeviceId, string Kind, string Target, string TargetKey, int? Minutes, string? Message,
        DateTimeOffset CreatedAt, string Status, DateTimeOffset? DecidedAt, string? CreatedRuleId, string? Title = null, string? Handle = null, string? SearchQuery = null);

    private sealed record RuntimeDto(DateTimeOffset? PausedUntil, DateTimeOffset? HomeworkUntil, List<GrantDto> Grants);

    public static string Grants(IEnumerable<TimeGrant> grants) =>
        JsonSerializer.Serialize(grants.Select(g => new GrantDto(g.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g.ExtraMinutes, g.TempWindowStart, g.TempWindowEnd)), Options);

    public static List<TimeGrant> Grants(string json) =>
        JsonSerializer.Deserialize<List<GrantDto>>(json, Options)!
            .Select(g => new TimeGrant(DateOnly.ParseExact(g.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), g.ExtraMinutes, g.TempWindowStart, g.TempWindowEnd)).ToList();

    public static string Runtime(IReadOnlyDictionary<string, MemberRuntime> runtime) =>
        JsonSerializer.Serialize(runtime.ToDictionary(kv => kv.Key, kv => new RuntimeDto(kv.Value.PausedUntil, kv.Value.HomeworkUntil,
            JsonSerializer.Deserialize<List<GrantDto>>(Grants(kv.Value.Grants), Options)!)), Options);

    public static Dictionary<string, MemberRuntime> Runtime(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, RuntimeDto>>(json, Options)!.ToDictionary(kv => kv.Key, kv => new MemberRuntime
        {
            PausedUntil = kv.Value.PausedUntil,
            HomeworkUntil = kv.Value.HomeworkUntil,
            Grants = Grants(JsonSerializer.Serialize(kv.Value.Grants, Options)),
        });

    public static string Event(FamilyEvent e) =>
        JsonSerializer.Serialize(new EventDto(e.Type.ToString(), e.At, e.MemberId, e.DeviceId, e.Reason == ReasonCode.None ? null : e.Reason.ToWire(), e.Url, e.App, e.RemainingMinutes, e.Detail), Options);

    public static FamilyEvent Event(string json)
    {
        var d = JsonSerializer.Deserialize<EventDto>(json, Options)!;
        return new FamilyEvent(Enum.Parse<EventType>(d.Type), d.At, d.MemberId, d.DeviceId, d.Reason is null ? ReasonCode.None : ReasonCodes.Parse(d.Reason), d.Url, d.App, d.RemainingMinutes, d.Detail);
    }

    public static string Request(AccessRequest r) =>
        JsonSerializer.Serialize(new RequestDto(r.Id, r.MemberId, r.DeviceId, r.Kind.ToString(), r.Target, r.TargetKey, r.Minutes, r.Message, r.CreatedAt, r.Status.ToString(), r.DecidedAt, r.CreatedRuleId,
            r.Title, r.Handle, r.SearchQuery), Options);

    public static AccessRequest Request(string json)
    {
        var d = JsonSerializer.Deserialize<RequestDto>(json, Options)!;
        return new AccessRequest
        {
            Id = d.Id, MemberId = d.MemberId, DeviceId = d.DeviceId, Kind = Enum.Parse<RequestKind>(d.Kind), Target = d.Target, TargetKey = d.TargetKey,
            Minutes = d.Minutes, Message = d.Message, CreatedAt = d.CreatedAt, Status = Enum.Parse<RequestStatus>(d.Status), DecidedAt = d.DecidedAt, CreatedRuleId = d.CreatedRuleId,
            Title = d.Title, Handle = d.Handle, SearchQuery = d.SearchQuery,
        };
    }

    public static long ToUnixMinute(DateTimeOffset minute) => minute.ToUnixTimeSeconds() / 60;

    public static DateTimeOffset FromUnixMinute(long minute) => DateTimeOffset.FromUnixTimeSeconds(minute * 60);

    public static string Instant(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    public static DateTimeOffset Instant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

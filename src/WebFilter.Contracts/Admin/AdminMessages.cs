using System.Text.Json;
using System.Text.Json.Serialization;
using WebFilter.Contracts.Framing;
using WebFilter.Contracts.Hub;

namespace WebFilter.Contracts.Admin;

/// <summary>
/// Admin protocol (MVP-PLAN §10.2): admin UI ↔ service over <c>\\.\pipe\WebFilter.Admin</c> (Administrators only).
/// Same framing and envelope as the agent protocol. Every request gets exactly one reply with the same <c>id</c>:
/// a specific result type, <see cref="AdminOk"/>, or <see cref="AdminError"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(GetStatus), "getStatus")]
[JsonDerivedType(typeof(StatusResult), "statusResult")]
[JsonDerivedType(typeof(GetSettings), "getSettings")]
[JsonDerivedType(typeof(SettingsResult), "settingsResult")]
[JsonDerivedType(typeof(ReplaceSettings), "replaceSettings")]
[JsonDerivedType(typeof(AddRule), "addRule")]
[JsonDerivedType(typeof(RemoveRule), "removeRule")]
[JsonDerivedType(typeof(SetMember), "setMember")]
[JsonDerivedType(typeof(ExtendTime), "extendTime")]
[JsonDerivedType(typeof(Pause), "pause")]
[JsonDerivedType(typeof(Resume), "resume")]
[JsonDerivedType(typeof(StartHomework), "startHomework")]
[JsonDerivedType(typeof(StopHomework), "stopHomework")]
[JsonDerivedType(typeof(GetEvents), "getEvents")]
[JsonDerivedType(typeof(EventsResult), "eventsResult")]
[JsonDerivedType(typeof(GetRequests), "getRequests")]
[JsonDerivedType(typeof(RequestsResult), "requestsResult")]
[JsonDerivedType(typeof(DecideRequest), "decideRequest")]
[JsonDerivedType(typeof(SetClock), "setClock")]
[JsonDerivedType(typeof(AdminOk), "ok")]
[JsonDerivedType(typeof(AdminError), "error")]
public abstract record AdminMessage
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")] public int Version { get; init; } = CurrentVersion;
    public string? Id { get; init; }
}

public sealed record GetStatus : AdminMessage;

public sealed record StatusResult(int SettingsVersion, string DeviceId, bool IsHub, DateTimeOffset Now, IReadOnlyList<MemberStatus> Members) : AdminMessage;

/// <param name="TimeState">ok | warn | expired | outside_window | unlimited.</param>
public sealed record MemberStatus(
    string MemberId,
    string DisplayName,
    string TimeState,
    int UsedMinutes,
    int? LimitMinutes,
    int? RemainingMinutes,
    string? WindowEndsAt,
    DateTimeOffset? PausedUntil,
    DateTimeOffset? HomeworkUntil);

public sealed record GetSettings : AdminMessage;

public sealed record SettingsResult(FamilyBundle Settings) : AdminMessage;

/// <summary>Replaces all settings (setup wizard, import). The bundle's version is ignored.</summary>
public sealed record ReplaceSettings(FamilyBundle Settings) : AdminMessage;

public sealed record AddRule(RuleDto Rule) : AdminMessage;

public sealed record RemoveRule(string RuleId) : AdminMessage;

/// <summary>Changes only the given fields of a family member.</summary>
public sealed record SetMember(string MemberId, string? DisplayName = null, string? Role = null, int? Age = null, string? FilterMode = null, string? YouTubeMode = null) : AdminMessage;

public sealed record ExtendTime(string MemberId, int Minutes) : AdminMessage;

public sealed record Pause(string MemberId, DateTimeOffset Until) : AdminMessage;

public sealed record Resume(string MemberId) : AdminMessage;

public sealed record StartHomework(string MemberId, int Minutes) : AdminMessage;

public sealed record StopHomework(string MemberId) : AdminMessage;

/// <param name="EventType">time_warning | time_expired | blocked | app_blocked | admin_change | access_request | tamper | service; null = all.</param>
public sealed record GetEvents(DateTimeOffset? Since = null, string? EventType = null, string? MemberId = null, int Max = 500) : AdminMessage;

public sealed record EventsResult(IReadOnlyList<EventDto> Events) : AdminMessage;

public sealed record EventDto(string Type, DateTimeOffset At, string? MemberId, string? DeviceId, string? Reason, string? Url, string? App, int? RemainingMinutes, string? Detail);

/// <param name="Status">pending | approved | denied; null = all.</param>
public sealed record GetRequests(string? Status = null, string? MemberId = null) : AdminMessage;

public sealed record RequestsResult(IReadOnlyList<RequestDto> Requests) : AdminMessage;

public sealed record RequestDto(
    string Id, string MemberId, string DeviceId, string Kind, string Target, string TargetKey, int? Minutes, string? Message,
    DateTimeOffset CreatedAt, string Status, DateTimeOffset? DecidedAt);

/// <param name="Decision">approve | deny.</param>
/// <param name="Scope">url requests: host (default) | page.</param>
/// <param name="Duration">today | always; or give <paramref name="Minutes"/>. For time requests, Minutes overrides the asked amount.</param>
public sealed record DecideRequest(string RequestId, string Decision, string? Scope = null, string? Duration = null, int? Minutes = null) : AdminMessage;

/// <summary>Simulation builds only (<c>#if SIMULATION</c>): sets the service clock. Release services answer "unsupported".</summary>
public sealed record SetClock(DateTimeOffset Now) : AdminMessage;

public sealed record AdminOk(string? Detail = null) : AdminMessage;

public sealed record AdminError(string Code, string Message) : AdminMessage;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(AdminMessage))]
public sealed partial class AdminJsonContext : JsonSerializerContext;

public static class AdminProtocol
{
    public static byte[] Serialize(AdminMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, AdminJsonContext.Default.AdminMessage);

    public static AdminMessage Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, AdminJsonContext.Default.AdminMessage) ?? throw new JsonException("Empty message");

    public static async Task<AdminMessage?> ReadAsync(Stream stream, CancellationToken ct = default) =>
        await MessageFraming.ReadAsync(stream, ct).ConfigureAwait(false) is { } frame ? Deserialize(frame) : null;

    public static Task WriteAsync(Stream stream, AdminMessage message, CancellationToken ct = default) =>
        MessageFraming.WriteAsync(stream, Serialize(message), ct);
}

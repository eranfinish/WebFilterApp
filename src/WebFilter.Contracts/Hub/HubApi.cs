using System.Text.Json;
using System.Text.Json.Serialization;
using WebFilter.Contracts.Admin;

namespace WebFilter.Contracts.Hub;

// Hub ↔ agent HTTPS API (MVP-PLAN §10.3). Platform-neutral JSON so an Android agent can use it too.
// Pairing (POST /v1/pair) is the only call without a client certificate; everything else requires mutual TLS
// with a certificate the hub issued at pairing.

/// <summary>
/// POST /v1/pair. <paramref name="Proof"/> = base64(HMAC-SHA256(key = pairing code, data = CSR bytes ‖ SHA-256 of the
/// hub CA certificate the agent saw in the TLS handshake)). A man in the middle presents another certificate, so the
/// proof fails without anyone learning the code.
/// </summary>
public sealed record PairRequest(string DeviceId, string DeviceName, string Os, string CsrBase64, string Proof);

/// <param name="Proof">base64(HMAC-SHA256(code, client certificate DER ‖ hub CA certificate DER)) — proves the hub knows the code too.</param>
public sealed record PairResponse(string HubId, string ClientCertificateBase64, string HubCaCertificateBase64, string Proof);

/// <summary>GET /v1/snapshot?since={version} → 204 when unchanged.</summary>
/// <param name="Decisions">
/// The requests the parents decided in the last 48 hours, newest first (at most 100), so the agent tells the member's
/// browsers on its computer (accessDecided) as the hub does on its own. Absent from an older hub: nothing to announce.
/// </param>
public sealed record SnapshotDto(int Version, FamilyBundle Settings, IReadOnlyDictionary<string, RuntimeDto> Runtime, IReadOnlyList<UsageDto> Usage,
    IReadOnlyList<DecisionDto>? Decisions = null);

public sealed record RuntimeDto(DateTimeOffset? PausedUntil, DateTimeOffset? HomeworkUntil, IReadOnlyList<GrantDto> Grants);

/// <param name="LocalDate">"yyyy-MM-dd" in the family time zone.</param>
public sealed record GrantDto(string LocalDate, int ExtraMinutes, int? TempWindowStart, int? TempWindowEnd);

/// <param name="Minutes">Whole minutes since the Unix epoch (UTC), sorted.</param>
public sealed record UsageDto(string Member, string Device, IReadOnlyList<long> Minutes);

/// <summary>A decided access request in a snapshot (<see cref="SnapshotDto.Decisions"/>).</summary>
/// <param name="Id">The request id the hub knows; for a request an agent queued, the id the agent gave it.</param>
/// <param name="Device">The computer the request came from.</param>
/// <param name="Kind">url | youtube | app | time.</param>
/// <param name="TargetKey">What merging compares: host without "www." · channel id · lowercase app path · "time:{date}".</param>
/// <param name="Status">approved | denied.</param>
/// <param name="Until">When the approval ends (the expiry of the rule it created); absent when it does not, and for a denial.</param>
public sealed record DecisionDto(string Id, string Member, string Device, string Kind, string TargetKey, string Status, DateTimeOffset DecidedAt, DateTimeOffset? Until = null);

/// <summary>POST /v1/upload: what the agent has not delivered yet. Idempotent: usage minutes merge; events and requests carry ids.</summary>
/// <param name="Accounts">Accounts seen on the device, so the hub can offer to link them to family members.</param>
public sealed record UploadDto(string DeviceId, IReadOnlyList<UsageDto> Usage, IReadOnlyList<UploadEventDto> Events, IReadOnlyList<RequestDto> QueuedRequests, IReadOnlyList<AccountDto>? Accounts = null);

/// <param name="EventId">Unique per device; the hub ignores ids it has already stored.</param>
public sealed record UploadEventDto(string EventId, EventDto Event);

public sealed record UploadResult(int SettingsVersion);

/// <summary>POST /v1/requests while the hub is reachable. <paramref name="Request"/>.Id is ignored; the hub assigns it.</summary>
public sealed record CreateRequestDto(RequestDto Request);

/// <param name="Result">created | merged | rate_limited | cooldown.</param>
public sealed record CreateRequestResult(string Result, string? RequestId);

/// <summary>GET /v1/changes: server-sent events, one "version" event per settings change.</summary>
public sealed record ChangeEvent(int Version);

/// <summary>
/// POST /v1/unregister: the device leaves the family by itself. The device is the one behind the client certificate, never
/// one named in the body. The hub revokes it, as a parent's revoke on the Devices screen would, and keeps the reason so
/// the parent sees why; the device stays listed, and pairing it again restores it. Answered 204; a device the hub no
/// longer accepts gets 401, like any other call.
/// </summary>
/// <param name="Reason">Why the device leaves; one of <see cref="Reasons"/>.</param>
public sealed record UnregisterRequest(string Reason)
{
    /// <summary>The device is being uninstalled.</summary>
    public const string Uninstall = "uninstall";

    public static readonly IReadOnlyList<string> Reasons = [Uninstall];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PairRequest))]
[JsonSerializable(typeof(PairResponse))]
[JsonSerializable(typeof(SnapshotDto))]
[JsonSerializable(typeof(UploadDto))]
[JsonSerializable(typeof(UploadResult))]
[JsonSerializable(typeof(CreateRequestDto))]
[JsonSerializable(typeof(CreateRequestResult))]
[JsonSerializable(typeof(ChangeEvent))]
[JsonSerializable(typeof(UnregisterRequest))]
public sealed partial class HubApiJsonContext : JsonSerializerContext;

public static class HubApi
{
    public const string PairPath = "/v1/pair";
    public const string SnapshotPath = "/v1/snapshot";
    public const string UploadPath = "/v1/upload";
    public const string RequestsPath = "/v1/requests";
    public const string ChangesPath = "/v1/changes";
    public const string UnregisterPath = "/v1/unregister";
    public const int DefaultPort = 47800;

    public static JsonSerializerOptions Options => HubApiJsonContext.Default.Options;
}

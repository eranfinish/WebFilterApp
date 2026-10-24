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
[JsonDerivedType(typeof(CreatePairingCode), "createPairingCode")]
[JsonDerivedType(typeof(PairingCodeResult), "pairingCodeResult")]
[JsonDerivedType(typeof(GetDevices), "getDevices")]
[JsonDerivedType(typeof(DevicesResult), "devicesResult")]
[JsonDerivedType(typeof(RevokeDevice), "revokeDevice")]
[JsonDerivedType(typeof(ScanAccounts), "scanAccounts")]
[JsonDerivedType(typeof(GetDiagnostics), "getDiagnostics")]
[JsonDerivedType(typeof(DiagnosticsResult), "diagnosticsResult")]
[JsonDerivedType(typeof(SetNetworksPrivate), "setNetworksPrivate")]
[JsonDerivedType(typeof(AdminOk), "ok")]
[JsonDerivedType(typeof(AdminError), "error")]
public abstract record AdminMessage
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")] public int Version { get; init; } = CurrentVersion;
    public string? Id { get; init; }
}

public sealed record GetStatus : AdminMessage;

/// <param name="TimeZone">The family's time zone (IANA id), for "until the end of the day"; null before an agent first hears from its hub.</param>
/// <param name="Hub">On an agent computer: the hub that manages it and whether it is reachable. Null on the hub.</param>
public sealed record StatusResult(int SettingsVersion, string DeviceId, bool IsHub, DateTimeOffset Now, IReadOnlyList<MemberStatus> Members,
    string? TimeZone = null, HubConnection? Hub = null) : AdminMessage;

/// <summary>An agent computer's link to the hub that manages it (MVP-PLAN §4.5: "managed by hub ‹name›").</summary>
/// <param name="Name">The hub computer's name (its device id in the settings); null until the first settings arrive.</param>
/// <param name="Address">The address the agent syncs with ("host:port"); null while this computer is not paired.</param>
/// <param name="Reachable">The last sync attempt succeeded.</param>
/// <param name="LastContactAt">The last successful sync since the service started.</param>
public sealed record HubConnection(string? Name, string? Address, bool Reachable, DateTimeOffset? LastContactAt);

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

/// <summary>Replaces all settings (setup wizard, member page, import). The bundle's own version is ignored.</summary>
/// <param name="IfVersion">
/// When set, the settings are replaced only if the hub is still at this settings version; otherwise the reply is an
/// <see cref="AdminError"/> with code <see cref="ConflictCode"/> and nothing changes, so an editor that read the settings
/// earlier cannot undo a change made meanwhile (a request approval, a newly paired computer). Read again and retry.
/// </param>
public sealed record ReplaceSettings(FamilyBundle Settings, int? IfVersion = null) : AdminMessage
{
    public const string ConflictCode = "conflict";
}

public sealed record AddRule(RuleDto Rule) : AdminMessage;

public sealed record RemoveRule(string RuleId) : AdminMessage;

/// <summary>
/// Changes only the given fields of a family member (null = unchanged), including the protection flags of PROTECTION-SPEC §1
/// and the YouTube channel search of §15.
/// </summary>
public sealed record SetMember(string MemberId, string? DisplayName = null, string? Role = null, int? Age = null, string? FilterMode = null, string? YouTubeMode = null,
    bool? BlockImageSearch = null, bool? JumpGuard = null, bool? BlockYouTubeShorts = null, bool? LogSearches = null, bool? BlockAds = null,
    [property: JsonPropertyName("youTubeChannelSearch")] bool? YouTubeChannelSearch = null) : AdminMessage;

public sealed record ExtendTime(string MemberId, int Minutes) : AdminMessage;

public sealed record Pause(string MemberId, DateTimeOffset Until) : AdminMessage;

public sealed record Resume(string MemberId) : AdminMessage;

public sealed record StartHomework(string MemberId, int Minutes) : AdminMessage;

public sealed record StopHomework(string MemberId) : AdminMessage;

/// <param name="EventType">
/// time_warning | time_expired | blocked | app_blocked | admin_change | access_request | tamper | service | search | override;
/// null = all.
/// </param>
public sealed record GetEvents(DateTimeOffset? Since = null, string? EventType = null, string? MemberId = null, int Max = 500) : AdminMessage;

public sealed record EventsResult(IReadOnlyList<EventDto> Events) : AdminMessage;

/// <summary>An activity event. By type (PROTECTION-SPEC §6, §10):</summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>search</c>: <c>detail</c> is "engine:query" — the engine (google, bing, youtube, duckduckgo; lower-case, never
/// a ':'), then everything after the first ':' is the query as typed; <c>url</c> is the results page.</item>
/// <item><c>override</c>: a teen chose "open anyway" on a jump block. <c>url</c> is the site opened, <c>reason</c>
/// <c>blocked_jump</c>.</item>
/// <item><c>tamper</c>: <c>detail</c> is policy_repaired, network_repaired, unclean_stop, or extension_missing:chrome /
/// extension_missing:edge (a browser cut off the network because it ran without the extension, PROTECTION-SPEC §14).</item>
/// </list>
/// </remarks>
/// <param name="Admin">For admin changes: the Windows user who made it.</param>
public sealed record EventDto(string Type, DateTimeOffset At, string? MemberId, string? DeviceId, string? Reason, string? Url, string? App, int? RemainingMinutes, string? Detail, string? Admin = null);

/// <param name="Status">pending | approved | denied; null = all.</param>
public sealed record GetRequests(string? Status = null, string? MemberId = null) : AdminMessage;

public sealed record RequestsResult(IReadOnlyList<RequestDto> Requests) : AdminMessage;

/// <summary>An access request (admin protocol, and the hub API's requests from agents).</summary>
/// <param name="Title">youtube: the channel's title, when the child's browser knew it (PROTECTION-SPEC §15). Absent from older services and agents.</param>
/// <param name="Handle">youtube: the channel's handle ("@name").</param>
/// <param name="SearchQuery">youtube: the channel search the child found the channel with.</param>
public sealed record RequestDto(
    string Id, string MemberId, string DeviceId, string Kind, string Target, string TargetKey, int? Minutes, string? Message,
    DateTimeOffset CreatedAt, string Status, DateTimeOffset? DecidedAt, string? Title = null, string? Handle = null, string? SearchQuery = null);

/// <param name="Decision">approve | deny.</param>
/// <param name="Scope">url requests: host (default) | page.</param>
/// <param name="Duration">today | always; or give <paramref name="Minutes"/>. For time requests, Minutes overrides the asked amount.</param>
public sealed record DecideRequest(string RequestId, string Decision, string? Scope = null, string? Duration = null, int? Minutes = null) : AdminMessage;

/// <summary>Simulation builds only (<c>#if SIMULATION</c>): sets the service clock. Release services answer "unsupported".</summary>
public sealed record SetClock(DateTimeOffset Now) : AdminMessage;

/// <summary>Hub only: a one-time code for pairing another computer (valid 10 minutes).</summary>
public sealed record CreatePairingCode : AdminMessage;

/// <param name="Addresses">This hub's IPv4 addresses on its networks, to type on the new computer together with <paramref name="Port"/>.</param>
public sealed record PairingCodeResult(string Code, DateTimeOffset ExpiresAt, int Port, IReadOnlyList<string> Addresses) : AdminMessage;

public sealed record GetDevices : AdminMessage;

public sealed record DevicesResult(IReadOnlyList<DeviceInfo> Devices) : AdminMessage;

/// <param name="RevokedReason">
/// Why a device that revoked itself left (<see cref="Hub.UnregisterRequest.Reasons"/>, e.g. "uninstall"); null when a
/// parent revoked it, or it is not revoked.
/// </param>
public sealed record DeviceInfo(string Id, string Name, string Os, DateTimeOffset PairedAt, DateTimeOffset? LastSeenAt, bool Revoked, string? RevokedReason = null);

/// <summary>Stops accepting a paired computer (it keeps enforcing its last settings until reinstalled or re-paired).</summary>
public sealed record RevokeDevice(string DeviceId) : AdminMessage;

/// <summary>
/// Hub only: reads this computer's Windows accounts and adds the ones the settings do not know yet to this computer's
/// device in the settings, not linked to anyone, so a parent can link them to family members (setup wizard). Accounts
/// already there keep their links. Replies <see cref="AdminOk"/> with the number of accounts added as its detail.
/// </summary>
public sealed record ScanAccounts : AdminMessage;

/// <summary>
/// Checks of this computer for the setup wizard's requirements step and the settings page (MVP-PLAN §4.5). Read only.
/// Answered on the hub and on agents.
/// </summary>
public sealed record GetDiagnostics : AdminMessage;

public sealed record DiagnosticsResult(IReadOnlyList<CheckResult> Checks) : AdminMessage;

/// <summary>
/// Hub only: sets the connected networks Windows marks Public to Private, so agents can reach the hub (its firewall rule is
/// for Private networks only). Answered with <see cref="AdminOk"/> whose detail lists the networks changed (comma-separated).
/// </summary>
public sealed record SetNetworksPrivate : AdminMessage;

/// <param name="Id">
/// service | browsers | browser_policies | extension | onedrive | network (hub) | hub_port (hub). onedrive: ok = installed
/// machine-wide (no detail); ok with detail "none" = not installed; warn = only per-user installs, which restricted accounts
/// cannot use (detail: the command that switches to a machine-wide install, "OneDriveSetup.exe /allusers").
/// </param>
/// <param name="State">ok | warn | fail.</param>
/// <param name="Detail">Facts the wording needs (browsers found, the public networks, the port) — not user-facing text.</param>
public sealed record CheckResult(string Id, string State, string? Detail = null);

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

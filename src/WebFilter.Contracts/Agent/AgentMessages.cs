using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebFilter.Contracts.Agent;

/// <summary>
/// Agent protocol (MVP-PLAN §10.1): extension ↔ native host ↔ service. Every message carries the protocol version
/// <c>v</c>, an optional correlation <c>id</c> (replies echo the request's id) and a <c>type</c> discriminator.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(HelloRequest), "hello")]
[JsonDerivedType(typeof(HelloResponse), "helloResult")]
[JsonDerivedType(typeof(GetRulesRequest), "getRules")]
[JsonDerivedType(typeof(RulesResponse), "rules")]
[JsonDerivedType(typeof(HeartbeatRequest), "heartbeat")]
[JsonDerivedType(typeof(TimeStateMessage), "timeState")]
[JsonDerivedType(typeof(ReportBlockRequest), "reportBlock")]
[JsonDerivedType(typeof(ReportBlockResult), "reportBlockResult")]
[JsonDerivedType(typeof(DecideUrlRequest), "decideUrl")]
[JsonDerivedType(typeof(DecideUrlResponse), "decideUrlResult")]
[JsonDerivedType(typeof(RequestAccessRequest), "requestAccess")]
[JsonDerivedType(typeof(RequestAccessResponse), "requestAccessResult")]
[JsonDerivedType(typeof(ResolveYouTubeRequest), "resolveYouTube")]
[JsonDerivedType(typeof(ResolveYouTubeResponse), "resolveYouTubeResult")]
[JsonDerivedType(typeof(RulesChangedMessage), "rulesChanged")]
[JsonDerivedType(typeof(AccessDecidedMessage), "accessDecided")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(ChunkMessage), "chunk")]
public abstract record AgentMessage
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")] public int Version { get; init; } = CurrentVersion;
    public string? Id { get; init; }
}

/// <param name="ExtensionId">chrome.runtime.id — used only to build the block-page URL in compiled rules.</param>
public sealed record HelloRequest(string ExtVersion, string Browser, string? ExtensionId = null) : AgentMessage;

/// <param name="Source">"service", or "host" when the native host answers by itself because the service is down.</param>
/// <param name="Mode">unrestricted | blocklist | allowlist — the extension disables its fail-closed ruleset for unrestricted users.</param>
public sealed record HelloResponse(
    AgentUser User,
    string Mode,
    string Source,
    bool ServiceAvailable,
    int RulesVersion,
    string ServerVersion) : AgentMessage;

/// <param name="AccountId">Opaque (a SID on Windows); the extension never interprets it.</param>
public sealed record AgentUser(string AccountId, string Name, string Role, bool IsAdmin);

public sealed record GetRulesRequest(int? KnownVersion = null) : AgentMessage;

/// <param name="DynamicRules">declarativeNetRequest rules, passed through verbatim to updateDynamicRules.</param>
/// <param name="TimeUpSessionRules">Installed with updateSessionRules while time is up.</param>
public sealed record RulesResponse(
    int RulesVersion,
    IReadOnlyList<JsonElement> DynamicRules,
    IReadOnlyList<JsonElement> TimeUpSessionRules,
    [property: JsonPropertyName("youtube")] YouTubePolicy YouTube,
    TimeStateMessage Time) : AgentMessage;

/// <param name="Mode">off | restricted | allowlist | unrestricted.</param>
public sealed record YouTubePolicy(string Mode, IReadOnlyList<YouTubeChannelRef> Allowed, IReadOnlyList<YouTubeChannelRef> Blocked);

public sealed record YouTubeChannelRef(string ChannelId, string? Handle, string? Title);

public sealed record HeartbeatRequest(bool Focused, string Idle, bool HasWebTab) : AgentMessage;

/// <param name="State">ok | warn | expired | outside_window | unlimited.</param>
public sealed record TimeStateMessage(string State, int? RemainingMinutes, string? WindowEndsAt) : AgentMessage;

public sealed record ReportBlockRequest(string Url, string Reason, string? RuleId) : AgentMessage;

/// <summary>
/// Reply to reportBlock: the service's own decision for the URL (the extension's reason is only a guess from its rules).
/// Decision "allow" means the extension's rules are stale; the service also pushes rulesChanged.
/// </summary>
public sealed record ReportBlockResult(string Decision, string Reason, string? RuleId, string? Category, bool CanRequest, string RequestKind) : AgentMessage;

/// <summary>A frame that is not a known message (see <see cref="AgentProtocol.ReadLenientAsync"/>). Never sent.</summary>
public sealed record UnknownMessage(string? Type) : AgentMessage;

/// <summary>The block page asks why <see cref="Url"/> is blocked, without recording a block (reportBlock records and also answers).</summary>
public sealed record DecideUrlRequest(string Url) : AgentMessage;

/// <param name="Decision">allow | block.</param>
/// <param name="Reason">A reason code wire name (README §5), e.g. blocked_rule, blocked_category, blocked_daily_limit.</param>
/// <param name="CanRequest">The user may send the parents a request (a linked family member).</param>
/// <param name="RequestKind">What a request should ask for: url, or time when the block is about time.</param>
public sealed record DecideUrlResponse(string Decision, string Reason, string? RuleId, string? Category, bool CanRequest, string RequestKind) : AgentMessage;

/// <param name="Kind">url | youtube | app | time.</param>
public sealed record RequestAccessRequest(string Kind, string? Target, int? Minutes, string? Note) : AgentMessage;

/// <param name="Result">created | merged | rate_limited | cooldown | queued | unavailable.</param>
public sealed record RequestAccessResponse(string Result, string? RequestId) : AgentMessage;

public sealed record ResolveYouTubeRequest(string? Handle, string? VideoId) : AgentMessage;

public sealed record ResolveYouTubeResponse(string? ChannelId) : AgentMessage;

public sealed record RulesChangedMessage(int RulesVersion) : AgentMessage;

/// <param name="Status">approved | denied.</param>
public sealed record AccessDecidedMessage(string RequestId, string Status, DateTimeOffset? Until) : AgentMessage;

public sealed record ErrorMessage(string Code, string Message) : AgentMessage;

/// <summary>
/// Part of a host→extension message larger than the browser's 1 MB limit. The native host splits the original frame's
/// UTF-8 JSON into base64 parts; the extension concatenates parts 0..Count-1 of the same <see cref="ChunkOf"/> and parses it.
/// </summary>
public sealed record ChunkMessage(string ChunkOf, int Index, int Count, string Data) : AgentMessage;

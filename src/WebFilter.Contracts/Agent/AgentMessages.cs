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
[JsonDerivedType(typeof(RequestAccessRequest), "requestAccess")]
[JsonDerivedType(typeof(RequestAccessResponse), "requestAccessResult")]
[JsonDerivedType(typeof(ResolveYouTubeRequest), "resolveYouTube")]
[JsonDerivedType(typeof(ResolveYouTubeResponse), "resolveYouTubeResult")]
[JsonDerivedType(typeof(RulesChangedMessage), "rulesChanged")]
[JsonDerivedType(typeof(AccessDecidedMessage), "accessDecided")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
public abstract record AgentMessage
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")] public int Version { get; init; } = CurrentVersion;
    public string? Id { get; init; }
}

public sealed record HelloRequest(string ExtVersion, string Browser) : AgentMessage;

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
    YouTubePolicy YouTube,
    TimeStateMessage Time) : AgentMessage;

/// <param name="Mode">off | restricted | allowlist | unrestricted.</param>
public sealed record YouTubePolicy(string Mode, IReadOnlyList<YouTubeChannelRef> Allowed, IReadOnlyList<YouTubeChannelRef> Blocked);

public sealed record YouTubeChannelRef(string ChannelId, string? Handle, string? Title);

public sealed record HeartbeatRequest(bool Focused, string Idle, bool HasWebTab) : AgentMessage;

/// <param name="State">ok | warn | expired | outside_window | unlimited.</param>
public sealed record TimeStateMessage(string State, int? RemainingMinutes, string? WindowEndsAt) : AgentMessage;

public sealed record ReportBlockRequest(string Url, string Reason, string? RuleId) : AgentMessage;

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

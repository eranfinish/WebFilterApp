using System.Text.Json;
using System.Text.Json.Serialization;
using WebFilter.Contracts.Framing;

namespace WebFilter.Contracts.Notify;

/// <summary>
/// Notification protocol (PROTECTION-SPEC §10): the notifier app on the hub computer ↔ the hub service over
/// <c>\\.\pipe\WebFilter.Notify</c> (<see cref="NotifyProtocol.PipeName"/>). Read only. Any signed-in user may open the
/// pipe, but the service answers only administrator accounts (elevated or not); anyone else gets one
/// <see cref="NotifyError"/> with code <see cref="NotifyError.NotAdmin"/> and the pipe is closed. Same framing and envelope
/// as the other pipes: <c>v</c>, <c>id</c> (echoed in the reply) and <c>type</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(GetNotifications), "getNotifications")]
[JsonDerivedType(typeof(NotificationsResult), "notificationsResult")]
[JsonDerivedType(typeof(NotifyError), "error")]
public abstract record NotifyMessage
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")] public int Version { get; init; } = CurrentVersion;
    public string? Id { get; init; }
}

/// <summary>
/// The notifications the hub recorded after <paramref name="Since"/> (exclusive), oldest first, at most
/// <see cref="NotificationsResult.MaxItems"/>; ask again from the last <c>at</c> for more. Without <paramref name="Since"/>:
/// all that are kept (30 days). A notifier starting for the first time passes the current time, or the last <c>at</c> it
/// showed. <c>at</c> is the time the hub recorded the notification, strictly increasing, so an item that arrives late (a
/// computer that was offline) is still after the cursor.
/// </summary>
public sealed record GetNotifications(DateTimeOffset? Since = null) : NotifyMessage;

public sealed record NotificationsResult(IReadOnlyList<NotificationDto> Items) : NotifyMessage
{
    public const int MaxItems = 100;
}

/// <summary>One thing a parent should know about.</summary>
/// <param name="Id">Stable and unique; the same notification is never listed under another id.</param>
/// <param name="Kind">
/// <see cref="NotificationKinds"/>: request | tamper | device | account. More may come: a notifier shows a kind it does not
/// know with generic wording.
/// </param>
/// <param name="At">When the hub recorded it (strictly increasing).</param>
/// <param name="MemberId">The family member it concerns, when there is one.</param>
/// <param name="DeviceId">The computer it concerns, when there is one.</param>
/// <param name="Detail">
/// request: "kind:target" — url:host, youtube:channelId, app:path or time:minutes (split at the first ':').
/// tamper: policy_repaired | network_repaired | unclean_stop (the tamper event's detail).
/// device: when the computer last reported (ISO-8601), or absent if it never did.
/// account: the Windows account's name.
/// </param>
public sealed record NotificationDto(string Id, string Kind, DateTimeOffset At, string? MemberId = null, string? DeviceId = null, string? Detail = null);

public static class NotificationKinds
{
    /// <summary>A new access request from a child.</summary>
    public const string Request = "request";

    /// <summary>Filters or policies were repaired, or the service found it had stopped uncleanly.</summary>
    public const string Tamper = "tamper";

    /// <summary>A paired computer has not reported to the hub for a day.</summary>
    public const string Device = "device";

    /// <summary>
    /// A new standard Windows account appeared on a computer (the hub or a paired one). Browsing there is blocked until a
    /// parent says whose it is, in the admin app's new-account card.
    /// </summary>
    public const string Account = "account";
}

/// <param name="Code"><see cref="NotAdmin"/>, "unsupported" (a message other than getNotifications) or "internal_error".</param>
public sealed record NotifyError(string Code, string Message) : NotifyMessage
{
    public const string NotAdmin = "not_admin";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(NotifyMessage))]
public sealed partial class NotifyJsonContext : JsonSerializerContext;

public static class NotifyProtocol
{
    /// <summary>The pipe's name (<c>\\.\pipe\WebFilter.Notify</c>).</summary>
    public const string PipeName = "WebFilter.Notify";

    public static byte[] Serialize(NotifyMessage message) => JsonSerializer.SerializeToUtf8Bytes(message, NotifyJsonContext.Default.NotifyMessage);

    public static NotifyMessage Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, NotifyJsonContext.Default.NotifyMessage) ?? throw new JsonException("Empty message");

    public static async Task<NotifyMessage?> ReadAsync(Stream stream, CancellationToken ct = default) =>
        await MessageFraming.ReadAsync(stream, ct).ConfigureAwait(false) is { } frame ? Deserialize(frame) : null;

    public static Task WriteAsync(Stream stream, NotifyMessage message, CancellationToken ct = default) =>
        MessageFraming.WriteAsync(stream, Serialize(message), ct);
}

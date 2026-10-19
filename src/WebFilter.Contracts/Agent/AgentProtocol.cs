using System.Text.Json;
using System.Text.Json.Serialization;
using WebFilter.Contracts.Framing;

namespace WebFilter.Contracts.Agent;

/// <summary>Source-generated serialization, so the NativeAOT native host needs no reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AgentMessage))]
public sealed partial class AgentJsonContext : JsonSerializerContext;

public static class AgentProtocol
{
    public static byte[] Serialize(AgentMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, AgentJsonContext.Default.AgentMessage);

    public static AgentMessage Deserialize(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize(utf8Json, AgentJsonContext.Default.AgentMessage) ?? throw new JsonException("Empty message");

    public static async Task<AgentMessage?> ReadAsync(Stream stream, CancellationToken ct = default) =>
        await MessageFraming.ReadAsync(stream, ct).ConfigureAwait(false) is { } frame ? Deserialize(frame) : null;

    /// <summary>
    /// Like <see cref="ReadAsync"/>, but a frame that is not a known message (a newer extension, a malformed message)
    /// comes back as <see cref="UnknownMessage"/> instead of throwing, so the caller can answer it and keep the session.
    /// </summary>
    public static async Task<AgentMessage?> ReadLenientAsync(Stream stream, CancellationToken ct = default)
    {
        if (await MessageFraming.ReadAsync(stream, ct).ConfigureAwait(false) is not { } frame) return null;
        try
        {
            return Deserialize(frame);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            string? type = null, id = null;
            try
            {
                using var doc = JsonDocument.Parse(frame);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String) type = t.GetString();
                    if (doc.RootElement.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String) id = i.GetString();
                }
            }
            catch (JsonException) { }
            return new UnknownMessage(type) { Id = id };
        }
    }

    public static Task WriteAsync(Stream stream, AgentMessage message, CancellationToken ct = default) =>
        MessageFraming.WriteAsync(stream, Serialize(message), ct);
}

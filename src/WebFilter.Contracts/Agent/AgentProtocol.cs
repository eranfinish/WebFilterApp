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

    public static Task WriteAsync(Stream stream, AgentMessage message, CancellationToken ct = default) =>
        MessageFraming.WriteAsync(stream, Serialize(message), ct);
}

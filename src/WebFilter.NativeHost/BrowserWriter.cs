using System.Text.Json;
using WebFilter.Contracts.Agent;
using WebFilter.Contracts.Framing;

namespace WebFilter.NativeHost;

/// <summary>Writes frames to the browser, splitting any frame over the 1 MB limit into <see cref="ChunkMessage"/>s.</summary>
internal sealed class BrowserWriter(Stream stdout)
{
    // Raw bytes per chunk: base64 grows them by 4/3, which stays well under the limit with the envelope.
    private const int RawChunkSize = 700 * 1024;

    public async Task WriteAsync(byte[] frame, CancellationToken ct)
    {
        if (frame.Length <= MessageFraming.MaxToBrowser)
        {
            await MessageFraming.WriteAsync(stdout, frame, ct);
            return;
        }

        var chunkOf = OriginalId(frame) ?? Guid.NewGuid().ToString("N");
        var count = (frame.Length + RawChunkSize - 1) / RawChunkSize;
        for (var i = 0; i < count; i++)
        {
            var part = frame.AsSpan(i * RawChunkSize, Math.Min(RawChunkSize, frame.Length - i * RawChunkSize));
            await AgentProtocol.WriteAsync(stdout, new ChunkMessage(chunkOf, i, count, Convert.ToBase64String(part)), ct);
        }
    }

    private static string? OriginalId(byte[] frame)
    {
        var reader = new Utf8JsonReader(frame);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isId = reader.ValueTextEquals("id"u8);
            if (!reader.Read()) return null;
            if (isId && reader.TokenType == JsonTokenType.String) return reader.GetString();
            reader.Skip();
        }
        return null;
    }
}

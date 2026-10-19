namespace WebFilter.Contracts.Framing;

/// <summary>
/// Native messaging framing, used unchanged on the agent pipe: a 4-byte little-endian length, then UTF-8 JSON.
/// Browsers cap host→extension messages at 1 MB; larger payloads must be chunked by the sender.
/// </summary>
public static class MessageFraming
{
    public const int MaxToBrowser = 1024 * 1024;
    public const int MaxFrame = 64 * 1024 * 1024;

    /// <summary>Reads one frame; null at a clean end of stream.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false)) return null;
        var length = BitConverter.ToInt32(header);
        if (length < 0 || length > MaxFrame) throw new InvalidDataException($"Frame length {length} is out of range");
        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false)) throw new EndOfStreamException("Stream ended inside a frame");
        return payload;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > MaxFrame) throw new InvalidDataException($"Frame length {payload.Length} is out of range");
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0) return false;
                throw new EndOfStreamException("Stream ended inside a frame");
            }
            offset += read;
        }
        return true;
    }
}

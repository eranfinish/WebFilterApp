namespace WebFilter.Spike;

// Native messaging framing, reused on the pipe: 4-byte little-endian length + UTF-8 JSON.
public static class Framing
{
    public const int MaxFrame = 64 * 1024 * 1024;

    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct)) return null;
        var length = BitConverter.ToInt32(header);
        if (length < 0 || length > MaxFrame) throw new InvalidDataException($"Frame length {length} out of range");
        var payload = new byte[length];
        return await ReadExactAsync(stream, payload, ct) ? payload : null;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length), ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}

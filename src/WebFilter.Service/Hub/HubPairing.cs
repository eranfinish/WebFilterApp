using System.Security.Cryptography;
using System.Text;

namespace WebFilter.Service.Hub;

/// <summary>
/// One-time pairing codes (MVP-PLAN §10.3): 12 characters from a 32-letter alphabet (≈60 bits), valid for 10 minutes,
/// usable once, kept in memory only. Both sides prove knowledge of the code with HMACs bound to what they saw.
/// </summary>
public sealed class HubPairing
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const string Alphabet = "ABCDEFGHJKMNPQRSTVWXYZ0123456789"; // no I, L, O, U
    private readonly object _gate = new();
    private readonly List<(string Code, DateTimeOffset Expires)> _codes = [];

    /// <summary>A new code, formatted for reading aloud: XXXX-XXXX-XXXX.</summary>
    public string Create(DateTimeOffset now)
    {
        var chars = RandomNumberGenerator.GetItems<char>(Alphabet, 12);
        var code = new string(chars);
        lock (_gate)
        {
            _codes.RemoveAll(c => c.Expires <= now);
            _codes.Add((code, now + Lifetime));
        }
        return $"{code[..4]}-{code[4..8]}-{code[8..]}";
    }

    public static string Normalize(string code) => new(code.ToUpperInvariant().Where(c => Alphabet.Contains(c)).ToArray());

    /// <summary>Finds and consumes the active code for which <paramref name="proof"/> = HMAC(code, data). Null if none matches.</summary>
    public string? Consume(byte[] data, byte[] proof, DateTimeOffset now)
    {
        lock (_gate)
        {
            _codes.RemoveAll(c => c.Expires <= now);
            foreach (var entry in _codes)
            {
                if (!CryptographicOperations.FixedTimeEquals(Mac(entry.Code, data), proof)) continue;
                _codes.Remove(entry);
                return entry.Code;
            }
        }
        return null;
    }

    public static byte[] Mac(string code, params byte[][] parts)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Normalize(code)));
        foreach (var part in parts) hmac.TransformBlock(part, 0, part.Length, null, 0);
        hmac.TransformFinalBlock([], 0, 0);
        return hmac.Hash!;
    }

    /// <summary>What the agent's proof covers: its CSR and the SHA-256 of the server certificate it saw.</summary>
    public static byte[] AgentProofData(byte[] csrDer, byte[] serverCertificateDer) => [.. csrDer, .. SHA256.HashData(serverCertificateDer)];
}

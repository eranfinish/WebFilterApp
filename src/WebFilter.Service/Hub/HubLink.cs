using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using WebFilter.Contracts.Hub;

namespace WebFilter.Service.Hub;

/// <summary>An agent's pairing with its hub: hub address, pinned hub CA, and the client certificate the hub issued.</summary>
public sealed class HubLink
{
    private sealed record LinkFile(string HubId, string Address, string HubCaBase64);

    public required string HubId { get; init; }
    public required string Address { get; init; }
    public required X509Certificate2 HubCa { get; init; }
    public required X509Certificate2 ClientCertificate { get; init; }

    private static string LinkPath(string dir) => Path.Combine(dir, "hub-link.json");
    private static string KeyPath(string dir) => Path.Combine(dir, "hub-client.pfx.dpapi");

    public static HubLink? Load(string dataDirectory)
    {
        if (!File.Exists(LinkPath(dataDirectory)) || !File.Exists(KeyPath(dataDirectory))) return null;
        var link = JsonSerializer.Deserialize<LinkFile>(File.ReadAllText(LinkPath(dataDirectory)))!;
        var pfx = ProtectedData.Unprotect(File.ReadAllBytes(KeyPath(dataDirectory)), null, DataProtectionScope.LocalMachine);
        return new HubLink
        {
            HubId = link.HubId,
            Address = link.Address,
            HubCa = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(link.HubCaBase64)),
            ClientCertificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable),
        };
    }

    /// <summary>
    /// Pairs with the hub at <paramref name="address"/> ("host:port") using the code a parent read from the hub.
    /// Throws if the code is wrong, the hub is not reachable, or the hub cannot prove it knows the code.
    /// </summary>
    public static async Task<HubLink> PairAsync(string dataDirectory, string address, string code, string deviceId, string deviceName, CancellationToken ct = default)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest($"CN=device:{deviceId}", key, HashAlgorithmName.SHA256).CreateSigningRequest();

        byte[]? serverCertificate = null;
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            // Accepted provisionally: the proof below binds the code to exactly this certificate.
            serverCertificate = certificate?.GetRawCertData();
            return certificate is not null;
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri($"https://{address}"), Timeout = TimeSpan.FromSeconds(15) };

        // A first request establishes TLS so we know the server certificate before computing the proof.
        using (var probe = new HttpRequestMessage(HttpMethod.Get, HubApi.SnapshotPath)) await http.SendAsync(probe, ct);
        if (serverCertificate is null) throw new InvalidOperationException("The hub did not present a certificate.");

        var proof = HubPairing.Mac(code, HubPairing.AgentProofData(csr, serverCertificate));
        using var response = await http.PostAsJsonAsync(HubApi.PairPath,
            new PairRequest(deviceId, deviceName, "windows", Convert.ToBase64String(csr), Convert.ToBase64String(proof)), HubApiJsonContext.Default.PairRequest, ct);
        if (!response.IsSuccessStatusCode) throw new UnauthorizedAccessException($"The hub refused the pairing ({(int)response.StatusCode}). Check the code.");
        var paired = await response.Content.ReadFromJsonAsync(HubApiJsonContext.Default.PairResponse, ct) ?? throw new InvalidOperationException("Empty pairing response");

        var clientDer = Convert.FromBase64String(paired.ClientCertificateBase64);
        var caDer = Convert.FromBase64String(paired.HubCaCertificateBase64);
        if (!CryptographicOperations.FixedTimeEquals(HubPairing.Mac(code, clientDer, caDer), Convert.FromBase64String(paired.Proof)))
            throw new UnauthorizedAccessException("The hub could not prove it knows the pairing code.");
        var ca = X509CertificateLoader.LoadCertificate(caDer);
        using var serverCert = X509CertificateLoader.LoadCertificate(serverCertificate);
        if (!HubCertificates.ChainsTo(serverCert, ca)) throw new UnauthorizedAccessException("The hub's certificate does not match its CA.");

        using var issued = X509CertificateLoader.LoadCertificate(clientDer);
        var client = X509CertificateLoader.LoadPkcs12(issued.CopyWithPrivateKey(key).Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);

        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(LinkPath(dataDirectory), JsonSerializer.Serialize(new LinkFile(paired.HubId, address, Convert.ToBase64String(caDer))));
        File.WriteAllBytes(KeyPath(dataDirectory), ProtectedData.Protect(client.Export(X509ContentType.Pkcs12), null, DataProtectionScope.LocalMachine));
        return new HubLink { HubId = paired.HubId, Address = address, HubCa = ca, ClientCertificate = client };
    }

    /// <summary>An HTTP client presenting our certificate and trusting only certificates issued by our hub's CA.</summary>
    public HttpClient CreateClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        handler.SslOptions.ClientCertificates = [ClientCertificate];
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && HubCertificates.ChainsTo(X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()), HubCa);
        return new HttpClient(handler) { BaseAddress = new Uri($"https://{Address}"), Timeout = timeout };
    }
}

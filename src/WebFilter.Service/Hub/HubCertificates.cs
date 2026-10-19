using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WebFilter.Service.Hub;

/// <summary>
/// The hub's own certificate authority, its TLS server certificate, and issuing agent client certificates at pairing.
/// Agents pin the CA (not host names), so the hub can be reached by any address on the home network. Private keys are
/// stored DPAPI-protected (machine scope) inside the service's data directory, which only SYSTEM and administrators can read.
/// </summary>
public sealed class HubCertificates
{
    private static readonly Oid ServerAuth = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid ClientAuth = new("1.3.6.1.5.5.7.3.2");

    public required string HubId { get; init; }
    public required X509Certificate2 Ca { get; init; }
    public required X509Certificate2 Server { get; init; }

    public static HubCertificates LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var caPath = Path.Combine(directory, "hub-ca.pfx.dpapi");
        var serverPath = Path.Combine(directory, "hub-server.pfx.dpapi");
        if (File.Exists(caPath) && File.Exists(serverPath))
        {
            var ca = Load(caPath);
            return new HubCertificates { HubId = HubIdOf(ca), Ca = ca, Server = Load(serverPath) };
        }

        var hubId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var created = Create(hubId);
        Save(caPath, created.Ca);
        Save(serverPath, created.Server);
        return created;
    }

    /// <summary>Signs an agent's certificate request. The subject is fixed to the device id; only client authentication is allowed.</summary>
    public X509Certificate2 IssueClientCertificate(byte[] csrDer, string deviceId)
    {
        var request = CertificateRequest.LoadSigningRequest(csrDer, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        var issued = new CertificateRequest(new X500DistinguishedName($"CN=device:{deviceId}"), request.PublicKey, HashAlgorithmName.SHA256);
        issued.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        issued.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        issued.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ClientAuth], false));
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        return issued.Create(Ca, now, now.AddYears(10), RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>True when <paramref name="certificate"/> was issued by this hub's CA.</summary>
    public bool Issued(X509Certificate2 certificate) => ChainsTo(certificate, Ca);

    public static bool ChainsTo(X509Certificate2 certificate, X509Certificate2 ca)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(certificate) && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(ca.RawData);
    }

    private static HubCertificates Create(string hubId)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest($"CN=WebFilter Hub CA {hubId}", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        var ca = caRequest.CreateSelfSigned(now, now.AddYears(20));

        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest($"CN=WebFilter Hub {hubId}", serverKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName.ToLowerInvariant());
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ServerAuth], false));
        using var serverPublic = serverRequest.Create(ca, now, now.AddYears(10), RandomNumberGenerator.GetBytes(16));
        var server = serverPublic.CopyWithPrivateKey(serverKey);

        // Re-import so the keys are persisted in a form Kestrel/SChannel can use.
        return new HubCertificates { HubId = hubId, Ca = Reimport(ca), Server = Reimport(server) };
    }

    private static string HubIdOf(X509Certificate2 ca) => ca.GetNameInfo(X509NameType.SimpleName, false).Split(' ')[^1];

    private static X509Certificate2 Reimport(X509Certificate2 certificate) =>
        X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable);

    private static void Save(string path, X509Certificate2 certificate) =>
        File.WriteAllBytes(path, ProtectedData.Protect(certificate.Export(X509ContentType.Pkcs12), null, DataProtectionScope.LocalMachine));

    private static X509Certificate2 Load(string path) =>
        X509CertificateLoader.LoadPkcs12(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.LocalMachine), null, X509KeyStorageFlags.Exportable);
}

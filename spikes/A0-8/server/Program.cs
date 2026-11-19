// Spike A0.8 harness (throwaway). Two modes:
//   serve <port> <dataDir> <familyFixture>  runs the real FamilyService + HubServer (as HubSyncTests does) and reads commands on stdin:
//                                          newcode | push | quit. Prints READY port=.. code=.. first.
//   vectors <outFile>                      writes proof vectors from the real HubPairing / HubCertificates code, for the Kotlin side to check.
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Service;
using WebFilter.Service.Hub;
using WebFilter.Service.Mapping;

if (args.Length == 2 && args[0] == "vectors") return Vectors(args[1]);
if (args.Length == 4 && args[0] == "serve") return await ServeAsync(int.Parse(args[1]), args[2], args[3]);
Console.Error.WriteLine("usage: A0_8Hub serve <port> <dataDir> <fixture> | vectors <outFile>");
return 2;

static async Task<int> ServeAsync(int port, string dataDir, string fixture)
{
    using var family = new FamilyService(Options.Create(new ServiceOptions
    {
        Role = "hub", DeviceId = "pc-parents", DataDirectory = dataDir, ProfileDirectory = Path.Combine(dataDir, "profiles"),
        HubBindAddress = "127.0.0.1", HubPort = port, BootstrapBundlePath = fixture,
    }), TimeProvider.System, NullLogger<FamilyService>.Instance);
    family.Start();

    var server = new HubServer(family, NullLogger<HubServer>.Instance);
    await server.StartAsync(CancellationToken.None);
    var bound = await server.Port.WaitAsync(TimeSpan.FromSeconds(30));
    Console.WriteLine($"READY port={bound} code={family.Pairing.Create(DateTimeOffset.UtcNow)}");

    string? line;
    while ((line = Console.ReadLine()) is not null)
    {
        switch (line.Trim())
        {
            case "newcode":
                Console.WriteLine($"CODE {family.Pairing.Create(DateTimeOffset.UtcNow)}");
                break;
            case "push":
                // ReplaceSettings bumps the version unconditionally (HubCore.cs:246); the SSE stream announces it.
                var version = family.WithHub((hub, now) =>
                {
                    hub.ReplaceSettings(BundleMapper.ToConfig(BundleMapper.ToBundle(hub.Config, hub.Version)), now);
                    return hub.Version;
                });
                Console.WriteLine($"PUSHED version={version}");
                break;
            case "quit":
                goto stop;
        }
    }
stop:
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    await server.StopAsync(timeout.Token);
    return 0;
}

static int Vectors(string outFile)
{
    var dir = Path.Combine(Path.GetTempPath(), "a0-8-vectors-" + Guid.NewGuid().ToString("N"));
    try
    {
        var certificates = HubCertificates.LoadOrCreate(dir);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=device:vector-device", key, HashAlgorithmName.SHA256).CreateSigningRequest();
        // Lowercase with dashes on purpose: the normalizer must turn this into ABCDEFGHJKMN before the HMAC key is taken.
        const string code = "abcd-efgh-jkmn";
        var serverLeaf = certificates.Server.RawData;
        var ca = certificates.Ca.RawData;

        using var issued = certificates.IssueClientCertificate(csr, "vector-device");
        var vectors = new Dictionary<string, string>
        {
            ["code"] = code,
            ["csrDerBase64"] = Convert.ToBase64String(csr),
            ["serverLeafDerBase64"] = Convert.ToBase64String(serverLeaf),
            ["caDerBase64"] = Convert.ToBase64String(ca),
            ["clientCertDerBase64"] = Convert.ToBase64String(issued.RawData),
            // Agent proof: HMAC(code, CSR || SHA-256(server LEAF)). The CA-hash variant is only a negative vector.
            ["agentProofBase64"] = Convert.ToBase64String(HubPairing.Mac(code, HubPairing.AgentProofData(csr, serverLeaf))),
            ["agentProofOverCaHashBase64"] = Convert.ToBase64String(HubPairing.Mac(code, HubPairing.AgentProofData(csr, ca))),
            // Hub proof: HMAC(code, client certificate DER || hub CA DER).
            ["responseProofBase64"] = Convert.ToBase64String(HubPairing.Mac(code, issued.RawData, ca)),
            // Finding, not a fix: the hub's own ChainsTo (used by HubLink.CreateClient for the hub certificate) accepts a device certificate.
            ["csharpChainsToAcceptsDeviceCertAsHub"] = HubCertificates.ChainsTo(issued, certificates.Ca).ToString(),
        };
        File.WriteAllText(outFile, JsonSerializer.Serialize(vectors, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"wrote {outFile}");
        return 0;
    }
    finally
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using WebFilter.Contracts.Hub;

namespace WebFilter.Service.Hub;

/// <summary>
/// The hub's HTTPS API for agents (MVP-PLAN §10.3), hosted with Kestrel inside the service. Pairing is the only call
/// without a client certificate; all others require one issued by this hub's CA and not revoked.
/// </summary>
public sealed class HubServer(FamilyService family, ILogger<HubServer> log) : BackgroundService
{
    private readonly TaskCompletionSource<int> _port = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The port actually listened on (useful when configured as 0).</summary>
    public Task<int> Port => _port.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!family.IsHub) return;
        var certificates = family.Certificates ?? throw new InvalidOperationException("Hub certificates are not loaded; call FamilyService.Start first.");
        var options = family.Options;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Parse(options.HubBindAddress), options.HubPort, listen => listen.UseHttps(https =>
        {
            https.ServerCertificate = certificates.Server;
            https.ServerCertificateChain = [certificates.Ca];
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.AllowAnyClientCertificate();
        })));
        var app = builder.Build();

        app.MapPost(HubApi.PairPath, async context =>
        {
            var request = await JsonSerializer.DeserializeAsync(context.Request.Body, HubApiJsonContext.Default.PairRequest, context.RequestAborted);
            if (request is null) { context.Response.StatusCode = 400; return; }
            var csr = Convert.FromBase64String(request.CsrBase64);
            var code = family.Pairing.Consume(HubPairing.AgentProofData(csr, certificates.Server.RawData), Convert.FromBase64String(request.Proof), family.Now);
            if (code is null)
            {
                log.LogWarning("Pairing refused for {Device}: no matching code", request.DeviceId);
                context.Response.StatusCode = 403;
                return;
            }
            using var issued = certificates.IssueClientCertificate(csr, request.DeviceId);
            family.RegisterPairedDevice(request.DeviceId, request.DeviceName, request.Os, issued.Thumbprint);
            var proof = HubPairing.Mac(code, issued.RawData, certificates.Ca.RawData);
            log.LogInformation("Paired device {Device} ({Name})", request.DeviceId, request.DeviceName);
            await context.Response.WriteAsJsonAsync(new PairResponse(certificates.HubId, Convert.ToBase64String(issued.RawData),
                Convert.ToBase64String(certificates.Ca.RawData), Convert.ToBase64String(proof)), HubApiJsonContext.Default.PairResponse);
        });

        app.MapGet(HubApi.SnapshotPath, async context =>
        {
            if (Authorize(context, certificates) is null) return;
            var since = int.TryParse(context.Request.Query["since"], out var v) ? v : -1;
            var snapshot = family.SnapshotSince(since);
            if (snapshot is null) { context.Response.StatusCode = 204; return; }
            await context.Response.WriteAsJsonAsync(snapshot, HubApiJsonContext.Default.SnapshotDto);
        });

        app.MapPost(HubApi.UploadPath, async context =>
        {
            if (Authorize(context, certificates) is not { } deviceId) return;
            var upload = await JsonSerializer.DeserializeAsync(context.Request.Body, HubApiJsonContext.Default.UploadDto, context.RequestAborted);
            if (upload is null) { context.Response.StatusCode = 400; return; }
            var version = family.IngestUpload(deviceId, upload);
            await context.Response.WriteAsJsonAsync(new UploadResult(version), HubApiJsonContext.Default.UploadResult);
        });

        app.MapPost(HubApi.RequestsPath, async context =>
        {
            if (Authorize(context, certificates) is not { } deviceId) return;
            var body = await JsonSerializer.DeserializeAsync(context.Request.Body, HubApiJsonContext.Default.CreateRequestDto, context.RequestAborted);
            if (body is null) { context.Response.StatusCode = 400; return; }
            await context.Response.WriteAsJsonAsync(family.CreateRequestFromAgent(deviceId, body.Request), HubApiJsonContext.Default.CreateRequestResult);
        });

        app.MapPost(HubApi.UnregisterPath, async context =>
        {
            if (Authorize(context, certificates) is not { } deviceId) return;
            var body = await JsonSerializer.DeserializeAsync(context.Request.Body, HubApiJsonContext.Default.UnregisterRequest, context.RequestAborted);
            if (body is null || !UnregisterRequest.Reasons.Contains(body.Reason)) { context.Response.StatusCode = 400; return; }
            // False only when a parent revoked it in the meantime: revoked either way.
            if (family.UnregisterDevice(deviceId, body.Reason)) log.LogInformation("Device {Device} unregistered itself ({Reason})", deviceId, body.Reason);
            context.Response.StatusCode = 204;
        });

        app.MapGet(HubApi.ChangesPath, async context =>
        {
            if (Authorize(context, certificates) is null) return;
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            var changes = Channel.CreateBounded<int>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });
            void OnChanged(int version) => changes.Writer.TryWrite(version);
            family.SettingsChanged += OnChanged;
            // Ends with the service as well: Kestrel waits for open requests when it stops, and agents keep this one open.
            using var open = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stoppingToken);
            var ended = open.Token;
            try
            {
                await context.Response.WriteAsync(": connected\n\n", ended);
                await context.Response.Body.FlushAsync(ended);
                while (!ended.IsCancellationRequested)
                {
                    using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(ended);
                    keepAlive.CancelAfter(TimeSpan.FromSeconds(25));
                    string line;
                    try
                    {
                        var version = await changes.Reader.ReadAsync(keepAlive.Token);
                        line = $"event: version\ndata: {JsonSerializer.Serialize(new ChangeEvent(version), HubApiJsonContext.Default.ChangeEvent)}\n\n";
                    }
                    catch (OperationCanceledException) when (!ended.IsCancellationRequested)
                    {
                        line = ": keep-alive\n\n";
                    }
                    await context.Response.WriteAsync(line, ended);
                    await context.Response.Body.FlushAsync(ended);
                }
            }
            catch (OperationCanceledException) { }
            finally { family.SettingsChanged -= OnChanged; }
        });

        await app.StartAsync(stoppingToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var port = new Uri(address.Replace("0.0.0.0", "localhost", StringComparison.Ordinal)).Port;
        log.LogInformation("Hub API listening on {Address}", address);
        _port.TrySetResult(port);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Bounded, so the service's stop stays well inside the host's shutdown timeout: past it the service crashed
            // (lab hub, 2026-10-26), Windows Error Reporting held one of its files, and an upgrade then needed a restart.
            // Requests still open after this are aborted.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
        }
    }

    /// <summary>The paired, unrevoked device behind the client certificate, or null after writing 401.</summary>
    private string? Authorize(HttpContext context, HubCertificates certificates)
    {
        var certificate = context.Connection.ClientCertificate;
        var device = certificate is not null && certificates.Issued(certificate) ? family.Devices!.ByThumbprint(certificate.Thumbprint) : null;
        if (device is null || device.RevokedAt is not null)
        {
            context.Response.StatusCode = 401;
            return null;
        }
        return device.Id;
    }
}

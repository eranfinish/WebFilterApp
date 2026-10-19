using System.Net;
using System.Net.Http.Json;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Engine;
using WebFilter.Service.Mapping;

namespace WebFilter.Service.Hub;

/// <summary>
/// Agent role: keeps the local agent in sync with the hub — pulls snapshots (immediately on a change event, otherwise
/// every <see cref="PollInterval"/>), uploads usage, events and queued requests, and sends access requests while the
/// hub is reachable. When it is not, the agent keeps enforcing its last snapshot and queues work.
/// </summary>
public sealed class HubSyncService(FamilyService family, ILogger<HubSyncService> log) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _wake = new(0);
    private HubLink? _link;
    private HttpClient? _http;

    /// <summary>Completed once the first sync with the hub succeeded (for tests and diagnostics).</summary>
    public TaskCompletionSource FirstSync { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (family.IsHub) return;
        while ((_link = HubLink.Load(family.Options.DataDirectory)) is null)
        {
            log.LogWarning("Not paired with a hub yet; run 'WebFilter.Service pair --hub host:port --code XXXX-XXXX-XXXX'");
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        _http = _link.CreateClient(TimeSpan.FromSeconds(10));
        family.UseRemoteRequestSender(SendRequest);
        var changes = ListenForChangesAsync(stoppingToken);

        var backoff = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncOnceAsync(stoppingToken);
                family.SetHubReachable(true);
                FirstSync.TrySetResult();
                backoff = TimeSpan.FromSeconds(1);
                await _wake.WaitAsync(PollInterval, stoppingToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException && !stoppingToken.IsCancellationRequested)
            {
                family.SetHubReachable(false);
                log.LogDebug(ex, "Hub unreachable; retrying in {Delay}", backoff);
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }
        await changes;
    }

    /// <summary>Asks for an immediate sync (after local changes worth delivering quickly).</summary>
    public void Wake() => _wake.Release();

    private async Task SyncOnceAsync(CancellationToken ct)
    {
        var upload = family.TakeUploadForHub();
        try
        {
            using var response = await _http!.PostAsJsonAsync(HubApi.UploadPath, SyncMapper.ToDto(upload, family.Now), HubApiJsonContext.Default.UploadDto, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("This device is no longer paired with the hub.");
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            family.RequeueUpload(upload);
            throw;
        }

        using var snapshotResponse = await _http!.GetAsync($"{HubApi.SnapshotPath}?since={family.AgentSnapshotVersion ?? -1}", ct);
        if (snapshotResponse.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("This device is no longer paired with the hub.");
        snapshotResponse.EnsureSuccessStatusCode();
        if (snapshotResponse.StatusCode == HttpStatusCode.NoContent) return;
        var snapshot = await snapshotResponse.Content.ReadFromJsonAsync(HubApiJsonContext.Default.SnapshotDto, ct);
        if (snapshot is not null) family.ApplyRemoteSnapshot(SyncMapper.FromDto(snapshot));
    }

    private async Task ListenForChangesAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = _link!.CreateClient(Timeout.InfiniteTimeSpan);
                using var response = await client.GetAsync(HubApi.ChangesPath, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
                while (await reader.ReadLineAsync(ct) is { } line)
                    if (line.StartsWith("data:", StringComparison.Ordinal)) _wake.Release();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogDebug(ex, "Change stream ended");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { }
        }
    }

    private (RequestResult, string?) SendRequest(AccessRequestDraft draft)
    {
        try
        {
            var dto = new CreateRequestDto(SyncMapper.ToDto(draft.ToRequest("pending")));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = _http!.PostAsJsonAsync(HubApi.RequestsPath, dto, HubApiJsonContext.Default.CreateRequestDto, cts.Token).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var result = response.Content.ReadFromJsonAsync(HubApiJsonContext.Default.CreateRequestResult, cts.Token).GetAwaiter().GetResult()!;
            return (result.Result switch
            {
                "created" => RequestResult.Created, "merged" => RequestResult.Merged, "rate_limited" => RequestResult.RateLimited, _ => RequestResult.Cooldown,
            }, result.RequestId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            log.LogDebug(ex, "Could not send the request to the hub; queued");
            return (RequestResult.Queued, null);
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Service.Accounts;
using WebFilter.Service.Mapping;

namespace WebFilter.Service.Hub;

/// <summary>
/// Agent role: keeps the local agent in sync with the hub — pulls snapshots (immediately on a change event, otherwise
/// every <see cref="PollInterval"/>), uploads usage, events, queued requests and this computer's Windows accounts (when
/// they changed), and sends access requests while the hub is reachable. When it is not, the agent keeps enforcing its
/// last snapshot and queues work. Hub role: reports the hub computer's own accounts into its settings.
/// </summary>
public sealed class HubSyncService(FamilyService family, ILogger<HubSyncService> log, ILocalAccounts? accounts = null) : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private readonly SemaphoreSlim _wake = new(0);
    private HubLink? _link;
    private HttpClient? _http;
    private IReadOnlyList<DeviceAccount>? _seen;
    private IReadOnlyList<DeviceAccount>? _reported;
    private long _seenAt;

    /// <summary>How often this computer's accounts are read again (accounts change rarely; reading them is cheap).</summary>
    internal TimeSpan AccountsInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Completed once the first sync with the hub succeeded (for tests and diagnostics).</summary>
    public TaskCompletionSource FirstSync { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (family.IsHub)
        {
            await ReportHubAccountsAsync(stoppingToken);
            return;
        }
        while ((_link = HubLink.Load(family.Options.DataDirectory)) is null)
        {
            log.LogWarning("Not paired with a hub yet; run 'WebFilter.Service pair --hub host:port --code XXXX-XXXX-XXXX'");
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        _http = _link.CreateClient(TimeSpan.FromSeconds(10));
        family.SetHubAddress(_link.Address);
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
        var accountsToReport = AccountsToReport();
        try
        {
            var dto = SyncMapper.ToDto(upload, family.Now) with
            {
                Accounts = accountsToReport?.Select(a => new AccountDto(a.AccountId, a.Name, null, a.IsAdmin)).ToList(),
            };
            using var response = await _http!.PostAsJsonAsync(HubApi.UploadPath, dto, HubApiJsonContext.Default.UploadDto, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("This device is no longer paired with the hub.");
            response.EnsureSuccessStatusCode();
            if (accountsToReport is not null) _reported = accountsToReport;
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

    /// <summary>The hub computer's accounts go straight into its own settings, whenever they change.</summary>
    private async Task ReportHubAccountsAsync(CancellationToken ct)
    {
        if (accounts is null) return;
        while (!ct.IsCancellationRequested)
        {
            if (AccountsToReport() is { } seen)
            {
                family.WithHub((hub, now) =>
                {
                    hub.ReportAccounts(family.DeviceId, seen, now);
                    return 0;
                });
                _reported = seen;
            }
            try { await Task.Delay(AccountsInterval, ct); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>This computer's accounts when they differ from the ones last reported (read at most every <see cref="AccountsInterval"/>).</summary>
    private IReadOnlyList<DeviceAccount>? AccountsToReport()
    {
        if (accounts is null) return null;
        if (_seen is null || Stopwatch.GetElapsedTime(_seenAt) >= AccountsInterval)
        {
            try
            {
                _seen = accounts.List();
                _seenAt = Stopwatch.GetTimestamp();
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException)
            {
                log.LogWarning(ex, "Could not read this computer's accounts");
                return null;
            }
        }
        return _reported is not null && Same(_seen, _reported) ? null : _seen;
    }

    private static bool Same(IReadOnlyList<DeviceAccount> a, IReadOnlyList<DeviceAccount> b) =>
        a.Count == b.Count && a.All(x => b.Any(y => string.Equals(x.AccountId, y.AccountId, StringComparison.OrdinalIgnoreCase) && x.Name == y.Name && x.IsAdmin == y.IsAdmin));

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

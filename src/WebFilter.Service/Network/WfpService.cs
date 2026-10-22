using System.ComponentModel;
using System.Runtime.Versioning;
using Microsoft.Extensions.Options;
using WebFilter.Core.Engine;

namespace WebFilter.Service.Network;

/// <summary>
/// Keeps the WFP filters equal to <see cref="NetworkPolicy.Desired"/> (MVP-PLAN §5.3): when the service starts, after
/// every settings change, and on every tick (<see cref="ServiceOptions.TickInterval"/>) — a time window that opens or
/// closes, or a daily limit that runs out, changes an account's allowed programs without any settings change, and the
/// tick also puts back filters someone deleted. Does nothing unless <see cref="ServiceOptions.ManageNetwork"/> is set.
/// On an agent it waits for the first settings from the hub; the persistent filters from before keep applying meanwhile.
/// A failure is logged and retried on the next tick; it never stops the service.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WfpService(FamilyService family, IOptions<ServiceOptions> options, ILogger<WfpService> log, IWfpEngine? engine = null) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>Completed after each pass (for tests): true when the filters were applied, false while waiting or after a failure.</summary>
    internal event Action<bool>? Passed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.ManageNetwork) return;
        var manager = new WfpManager(engine ?? new NativeWfpEngine());
        void OnSettings(int _) => Wake();
        void OnTick(DateTimeOffset _) => Wake();
        family.SettingsChanged += OnSettings;
        family.Ticked += OnTick;
        var waitingLogged = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var applied = false;
                try
                {
                    // An agent has no family settings until its first sync with the hub: change nothing yet rather than
                    // act on an empty family (which would lift every filter).
                    // Resolved on every pass: a machine-wide OneDrive installed meanwhile is picked up.
                    var programs = WindowsProgramPaths.Resolve();
                    var desired = family.WithAgent((agent, now) => agent.SnapshotVersion is null ? null : NetworkPolicy.Desired(agent.View(), agent.DeviceId, now, programs));
                    if (desired is null)
                    {
                        if (!waitingLogged) log.LogInformation("Network filters wait for the first settings from the hub");
                        waitingLogged = true;
                    }
                    else
                    {
                        var result = manager.Apply(desired);
                        Report(result);
                        applied = result.Failed.Count == 0;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    log.LogError(ex, "Could not apply the network filters; retrying on the next tick");
                }
                Passed?.Invoke(applied);
                await _wake.WaitAsync(Timeout.InfiniteTimeSpan, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            family.SettingsChanged -= OnSettings;
            family.Ticked -= OnTick;
        }
    }

    /// <summary>One pending wake-up is enough: a pass reads the latest settings and time.</summary>
    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    private void Report(WfpManager.ApplyResult result)
    {
        foreach (var (sid, error) in result.Failed) log.LogError(error, "Could not set the network filters of {Account}; retrying on the next tick", sid);
        foreach (var sid in result.Repaired)
        {
            log.LogWarning("The network filters of {Account} had been removed; restored", sid);
            family.ReportTamper(sid, TamperKinds.NetworkRepaired);
        }
        foreach (var (sid, app) in result.Skipped)
        {
            // Not installed is normal (the machine-wide Chrome or OneDrive on a computer without them): the program gets its
            // filter once it is installed. Anything else is a real problem.
            if (app.NotInstalled)
                log.LogInformation("{App}, allowed for {Account}, is not installed; it gets a network filter once it is", app.Value, sid);
            else
                log.LogWarning("{App}, allowed for {Account}, got no network filter and stays blocked: {Reason}", app.Value, sid, new Win32Exception(app.Error).Message);
        }
        if (result.Set + result.Removed > 0) log.LogInformation("Network filters: {Set} accounts set, {Removed} removed", result.Set, result.Removed);
    }
}

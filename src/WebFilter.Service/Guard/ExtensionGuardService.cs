using Microsoft.Extensions.Options;
using WebFilter.Core.Engine;
using WebFilter.Service.Network;

namespace WebFilter.Service.Guard;

/// <summary>
/// The browsers the extension guard keeps off the network, by restricted account (PROTECTION-SPEC §14): set by
/// <see cref="ExtensionGuardService"/>, read by <see cref="WfpService"/> on every pass (<see cref="NetworkPolicy.Desired"/>
/// leaves their paths out of that account's allowlist). Empty until the guard has run, so a restarted service starts with
/// every browser allowed and a new download window.
/// </summary>
public sealed class BrowserBlocks
{
    private IReadOnlyList<AccountBrowser> _current = [];

    public IReadOnlyList<AccountBrowser> Current => Volatile.Read(ref _current);

    /// <summary>Raised when the set changes, so the network filters follow at once.</summary>
    public event Action? Changed;

    /// <summary>Replaces the set (sorted, as <see cref="ExtensionGuardResult.Blocked"/> is); false when nothing changed.</summary>
    public bool Set(IReadOnlyList<AccountBrowser> blocked)
    {
        if (blocked.SequenceEqual(Current)) return false;
        Volatile.Write(ref _current, blocked);
        Changed?.Invoke();
        return true;
    }
}

/// <summary>
/// Runs the extension guard (PROTECTION-SPEC §14, <see cref="ExtensionGuard"/>): every
/// <see cref="ServiceOptions.ExtensionGuardInterval"/> (10 seconds), after every settings change, and at once when an
/// extension says hello (so a blocked browser gets its network back without waiting), it finds the machine-wide Chrome and
/// Edge processes of every account (<see cref="IRunningPrograms"/>), takes the extension's sessions from the agent pipe
/// (<see cref="ExtensionConnections"/>), and hands the browsers to block to <see cref="WfpService"/>
/// (<see cref="BrowserBlocks"/>). A block that starts is a tamper event for the parents
/// (<see cref="TamperKinds.ExtensionMissing"/>, at most once an hour per browser and account: <see cref="FamilyService.ReportTamper"/>).
/// Polling, not process events: a browser's start matters only after the download window, so 10 seconds late costs
/// nothing, and a poll also sees a browser that started while the service was busy or stopped.
/// Does nothing unless <see cref="ServiceOptions.ManageNetwork"/> and <see cref="ServiceOptions.ExtensionGuard"/> are set.
/// On an agent it waits for the first settings from the hub. A failure is logged and retried on the next pass, keeping the
/// blocks it had; it never stops the service.
/// </summary>
public sealed class ExtensionGuardService(FamilyService family, ExtensionConnections connections, BrowserBlocks blocks, IOptions<ServiceOptions> options,
    ILogger<ExtensionGuardService> log, IRunningPrograms? programs = null) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>Completed after each pass (for tests): the guard's result, or null while waiting for settings or after a failure.</summary>
    internal event Action<ExtensionGuardResult?>? Passed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.ManageNetwork || !settings.ExtensionGuard) return;
        var guard = new ExtensionGuard(new ExtensionGuardOptions(settings.ExtensionGrace, ExtensionGuardOptions.DefaultStaleness,
            settings.ChromeExtensionId, settings.EdgeExtensionId));
        var running = programs ?? new WindowsRunningPrograms();
        void OnSettings(int _) => Wake();
        connections.Connected += Wake;
        family.SettingsChanged += OnSettings;
        log.LogInformation("Extension guard on: a browser without a live extension connection loses its network after {Grace}", settings.ExtensionGrace);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                ExtensionGuardResult? result = null;
                try
                {
                    result = Pass(guard, running);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    log.LogError(ex, "The extension guard could not check the browsers; retrying on the next pass");
                }
                Passed?.Invoke(result);
                await _wake.WaitAsync(settings.ExtensionGuardInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            connections.Connected -= Wake;
            family.SettingsChanged -= OnSettings;
        }
    }

    private ExtensionGuardResult? Pass(ExtensionGuard guard, IRunningPrograms running)
    {
        // Resolved on every pass, like the network filters: a browser installed meanwhile is watched.
        var machine = WindowsProgramPaths.Resolve();
        var input = family.WithAgent<(IReadOnlyList<string> Accounts, Dictionary<string, BrowserKind> Paths, DateTimeOffset Now)?>((agent, now) =>
        {
            // An agent has no family settings until its first sync with the hub: nothing is guarded yet.
            if (agent.SnapshotVersion is null) return null;
            var view = agent.View();
            var paths = new Dictionary<string, BrowserKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var browser in BrowserKinds.All)
                foreach (var path in browser.Paths(view.Config, machine)) paths.TryAdd(path, browser);
            return (ExtensionGuard.GuardedAccounts(view, agent.DeviceId, now), paths, now);
        });
        if (input is not { } value) return null;
        var (accounts, paths, now) = value;

        List<BrowserProcess> processes = accounts.Count == 0 ? [] : running.Find(paths.Keys)
            .Where(p => paths.ContainsKey(p.Path))
            .Select(p => new BrowserProcess(p.UserSid, paths[p.Path], p.ProcessId, p.StartedAt))
            .ToList();
        var result = guard.Evaluate(accounts, processes, connections.Snapshot(now), now);
        Report(result);
        blocks.Set(result.Blocked);
        return result;
    }

    private void Report(ExtensionGuardResult result)
    {
        foreach (var (account, browser) in result.Started)
        {
            log.LogWarning("{Browser} runs in {Account} without a live connection from the WebFilter extension after the download window; its network is blocked until the extension connects",
                browser, account);
            family.ReportTamper(account, TamperKinds.ExtensionMissingIn(browser));
        }
        foreach (var (account, browser) in result.Restored)
            log.LogInformation("The WebFilter extension connected from {Browser} in {Account}; its network is allowed again", browser, account);
        foreach (var (account, browser) in result.Ended)
            log.LogInformation("{Browser} stopped or started again in {Account} (or the account is no longer filtered); its network block is lifted", browser, account);
    }

    /// <summary>A pass now, without waiting for the interval (tests).</summary>
    internal void Poke() => Wake();

    /// <summary>One pending wake-up is enough: a pass reads the latest processes, connections and settings.</summary>
    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }
}

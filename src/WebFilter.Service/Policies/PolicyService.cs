using System.Runtime.Versioning;
using Microsoft.Extensions.Options;

namespace WebFilter.Service.Policies;

/// <summary>
/// Applies the browser policies when the service starts, after every settings change, and every
/// <see cref="ServiceOptions.PolicyInterval"/> (which repairs tampering and covers users who logged on since).
/// Does nothing unless <see cref="ServiceOptions.ManageBrowserPolicies"/> is set. On an agent it waits for the first
/// settings from the hub. A failure is logged and retried on the next pass; it never stops the service.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PolicyService(FamilyService family, IOptions<ServiceOptions> options, ILogger<PolicyService> log, IPolicyStore? store = null)
    : BackgroundService
{
    /// <summary>In <see cref="ServiceOptions.DataDirectory"/>: what <see cref="PolicyManager"/> wrote (read again by uninstall).</summary>
    public const string StateFileName = "managed-policies.json";

    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>Completed after each pass (for tests): true when policies were applied, false while waiting for settings.</summary>
    internal event Action<bool>? Passed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.ManageBrowserPolicies) return;
        var manager = new PolicyManager(store ?? new RegistryPolicyStore(), Path.Combine(settings.DataDirectory, StateFileName));
        var ids = new ExtensionIds(settings.ChromeExtensionId, settings.EdgeExtensionId);
        void OnSettings(int _) => _wake.Release();
        family.SettingsChanged += OnSettings;
        var waitingLogged = false;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var applied = false;
                try
                {
                    // An agent has no family settings until its first sync with the hub (a fresh install, or a hub that is
                    // off or unreachable): write nothing yet rather than act on an empty family.
                    var desired = family.WithAgent((agent, _) => agent.SnapshotVersion is null
                        ? null
                        : BrowserPolicies.Desired(agent.View().Config, agent.DeviceId, ids, settings.NativeHostManifestPath));
                    if (desired is null)
                    {
                        if (!waitingLogged) log.LogInformation("Browser policies wait for the first settings from the hub");
                        waitingLogged = true;
                    }
                    else
                    {
                        var result = manager.Apply(desired);
                        foreach (var value in result.Repaired) log.LogWarning("Browser policy {Value} had been changed; restored", value);
                        if (result.Written + result.Deleted > 0) log.LogInformation("Browser policies: {Written} written, {Deleted} removed", result.Written, result.Deleted);
                        applied = true;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "Could not apply browser policies; retrying on the next pass");
                }
                Passed?.Invoke(applied);
                await _wake.WaitAsync(settings.PolicyInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            family.SettingsChanged -= OnSettings;
        }
    }
}

using System.Runtime.Versioning;
using Microsoft.Extensions.Options;

namespace WebFilter.Service.Policies;

/// <summary>
/// Applies the browser policies when the service starts, after every settings change, and every
/// <see cref="ServiceOptions.PolicyInterval"/> (which repairs tampering and covers users who logged on since).
/// Does nothing unless <see cref="ServiceOptions.ManageBrowserPolicies"/> is set.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PolicyService(FamilyService family, IOptions<ServiceOptions> options, ILogger<PolicyService> log) : BackgroundService
{
    /// <summary>In <see cref="ServiceOptions.DataDirectory"/>: what <see cref="PolicyManager"/> wrote (read again by uninstall).</summary>
    public const string StateFileName = "managed-policies.json";

    private readonly SemaphoreSlim _wake = new(0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.ManageBrowserPolicies) return;
        var manager = new PolicyManager(new RegistryPolicyStore(), Path.Combine(settings.DataDirectory, StateFileName));
        var ids = new ExtensionIds(settings.ChromeExtensionId, settings.EdgeExtensionId);
        void OnSettings(int _) => _wake.Release();
        family.SettingsChanged += OnSettings;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var desired = family.WithAgent((agent, _) => BrowserPolicies.Desired(agent.View().Config, agent.DeviceId, ids, settings.NativeHostManifestPath));
                    var result = manager.Apply(desired);
                    foreach (var value in result.Repaired) log.LogWarning("Browser policy {Value} had been changed; restored", value);
                    if (result.Written + result.Deleted > 0) log.LogInformation("Browser policies: {Written} written, {Deleted} removed", result.Written, result.Deleted);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                {
                    log.LogError(ex, "Could not apply browser policies");
                }
                await _wake.WaitAsync(settings.PolicyInterval, stoppingToken);
            }
        }
        finally
        {
            family.SettingsChanged -= OnSettings;
        }
    }
}

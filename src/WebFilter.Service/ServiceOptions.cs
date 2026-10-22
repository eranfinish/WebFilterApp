namespace WebFilter.Service;

/// <summary>Configuration (appsettings.json section "WebFilter", or command-line overrides).</summary>
public sealed class ServiceOptions
{
    /// <summary>The Windows service name (the installer registers the service under it).</summary>
    public const string WindowsServiceName = "WebFilter";

    /// <summary>%ProgramData%\WebFilter: everything the service stores. The installer locks it to SYSTEM and Administrators; uninstall deletes it.</summary>
    public static string MachineDataRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WebFilter");

    /// <summary>"hub" (this PC owns the settings; also enforces locally) or "agent" (syncs with a hub — M6).</summary>
    public string Role { get; set; } = "hub";

    /// <summary>This device's id in the family settings.</summary>
    public string DeviceId { get; set; } = Environment.MachineName.ToLowerInvariant();

    public string DataDirectory { get; set; } = Path.Combine(MachineDataRoot, "data");

    /// <summary>Where the native host finds each account's <see cref="Contracts.Agent.ProfileCache"/>.</summary>
    public string ProfileDirectory { get; set; } = Path.Combine(MachineDataRoot, "profiles");

    public string AgentPipeName { get; set; } = "WebFilter.Agent";

    public string AdminPipeName { get; set; } = "WebFilter.Admin";

    /// <summary>Executables allowed to speak the agent protocol (the native host). Compared case-insensitively.</summary>
    public List<string> TrustedClientPaths { get; set; } =
        [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WebFilter", "WebFilter.NativeHost.exe")];

    /// <summary>Settings used the first time the hub starts with no stored state (until the setup wizard exists).</summary>
    public string? BootstrapBundlePath { get; set; }

    /// <summary>Hub: the HTTPS port agents connect to (0 = any free port, for tests).</summary>
    public int HubPort { get; set; } = Contracts.Hub.HubApi.DefaultPort;

    /// <summary>Hub: the address to listen on. The installer opens the port for Private networks only.</summary>
    public string HubBindAddress { get; set; } = "0.0.0.0";

    /// <summary>How often time warnings are evaluated and stored state is flushed.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Write the browser policies of MVP-PLAN §5 to the registry. True in the installed service (LocalSystem); false for
    /// development runs, which cannot and must not change machine policy.
    /// </summary>
    public bool ManageBrowserPolicies { get; set; }

    /// <summary>How often the registry policies are checked and repaired, besides every settings change.</summary>
    public TimeSpan PolicyInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>After a user logs on: the gap between the extra policy passes that cover a hive Windows loads late.</summary>
    public TimeSpan LogonRecheck { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Store ids of the published extension (after spike S1). Without them no force-install policy is written.</summary>
    public string? ChromeExtensionId { get; set; }

    public string? EdgeExtensionId { get; set; }

    /// <summary>The native messaging host manifest the installer places next to the host.</summary>
    public string NativeHostManifestPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WebFilter", "com.webfilter.host.json");
}

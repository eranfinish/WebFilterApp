using System.Security.Principal;
using Microsoft.Win32;
using WebFilter.Contracts.Admin;

namespace WebFilter.Service.Setup;

/// <summary>What the requirement checks read from this computer — read only, nothing is changed.</summary>
public interface IMachineProbe
{
    /// <summary>The service runs as LocalSystem (installed), not as a developer's account.</summary>
    bool RunsAsLocalSystem { get; }

    /// <summary>The supported browsers installed: "edge", "chrome".</summary>
    IReadOnlyList<string> InstalledBrowsers();

    /// <summary>A REG_SZ under HKLM, or null.</summary>
    string? MachineValue(string keyPath, string name);

    /// <summary>The networks this computer is connected to, with Windows' category: public, private or domain.</summary>
    IReadOnlyList<(string Name, string Category)> ConnectedNetworks();

    /// <summary>Whether an enabled inbound firewall rule allows TCP <paramref name="port"/>; null when that cannot be read.</summary>
    bool? InboundPortAllowed(int port);

    /// <summary>Where OneDrive is installed: machine-wide (Program Files), and/or per user (in a profile's AppData\Local).</summary>
    (bool MachineWide, bool PerUser) OneDriveInstalls();
}

/// <summary>
/// The requirement checks of MVP-PLAN §4.5 (setup wizard) and the settings page's diagnostics, as wire codes; the admin
/// app words them. The two hub checks are why agents could not reach the hub in the first lab install (a Public network).
/// </summary>
public static class MachineChecks
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Fail = "fail";

    /// <summary>The onedrive check's detail when only per-user installs exist: how to switch to a machine-wide install.</summary>
    public const string OneDriveSwitchCommand = "OneDriveSetup.exe /allusers";

    /// <summary>The onedrive check's detail when OneDrive is not installed at all.</summary>
    public const string OneDriveNone = "none";

    public static IReadOnlyList<CheckResult> Run(ServiceOptions options, bool isHub, IMachineProbe probe)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(probe);
        var checks = new List<CheckResult>
        {
            new("service", probe.RunsAsLocalSystem ? Ok : Warn),
            probe.InstalledBrowsers() is { Count: > 0 } browsers ? new("browsers", Ok, string.Join(",", browsers)) : new("browsers", Fail),
            new("browser_policies", options.ManageBrowserPolicies ? Ok : Warn),
            Extension(options, probe),
            OneDrive(probe),
        };
        if (!isHub) return checks;

        var networks = probe.ConnectedNetworks();
        var publicOnes = networks.Where(n => n.Category == "public").Select(n => n.Name).ToList();
        checks.Add(networks.Count == 0 ? new("network", Fail)
            : publicOnes.Count > 0 ? new("network", Warn, string.Join(", ", publicOnes))
            : new("network", Ok));
        if (probe.InboundPortAllowed(options.HubPort) is { } open)
            checks.Add(new("hub_port", open ? Ok : Warn, options.HubPort.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return checks;
    }

    /// <summary>Force-installed in at least one browser; a warning while the store ids are not known yet (spike S1).</summary>
    private static CheckResult Extension(ServiceOptions options, IMachineProbe probe)
    {
        (string? Id, string Key)[] browsers =
        [
            (options.ChromeExtensionId, @"SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist"),
            (options.EdgeExtensionId, @"SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist"),
        ];
        if (browsers.All(b => string.IsNullOrEmpty(b.Id))) return new("extension", Warn);
        var installed = browsers.Any(b => !string.IsNullOrEmpty(b.Id)
                                          && probe.MachineValue(b.Key, "1") is { } value && value.StartsWith(b.Id + ";", StringComparison.OrdinalIgnoreCase));
        return new("extension", installed ? Ok : Fail);
    }

    /// <summary>
    /// PROTECTION-SPEC §2: restricted accounts may use only a machine-wide OneDrive (a per-user one lives where the child can
    /// write, so it never gets network access). Only per-user installs → a warning with the command that switches.
    /// </summary>
    private static CheckResult OneDrive(IMachineProbe probe)
    {
        var (machineWide, perUser) = probe.OneDriveInstalls();
        return machineWide ? new("onedrive", Ok)
            : perUser ? new("onedrive", Warn, OneDriveSwitchCommand)
            : new("onedrive", Ok, OneDriveNone);
    }
}

/// <summary>Reads this Windows computer (registry, Network List Manager, firewall rules); never writes.</summary>
public sealed class WindowsMachineProbe : IMachineProbe
{
    private static readonly Guid NetworkListManager = new("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

    public bool RunsAsLocalSystem => WindowsIdentity.GetCurrent().IsSystem;

    public IReadOnlyList<string> InstalledBrowsers()
    {
        string[] programFiles = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
        bool Exists(params string[] relative) => programFiles.Any(root => !string.IsNullOrEmpty(root) && File.Exists(Path.Combine([root, .. relative])));
        var found = new List<string>();
        if (Exists("Microsoft", "Edge", "Application", "msedge.exe")) found.Add("edge");
        if (Exists("Google", "Chrome", "Application", "chrome.exe")) found.Add("chrome");
        return found;
    }

    public (bool MachineWide, bool PerUser) OneDriveInstalls()
    {
        string[] programFiles = [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];
        var machineWide = programFiles.Any(root => !string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "Microsoft OneDrive", "OneDrive.exe")));
        var perUser = false;
        try
        {
            // Every local profile (ProfileList), whether or not its user is logged on.
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var profiles = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            foreach (var sid in profiles?.GetSubKeyNames() ?? [])
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue;
                using var profile = profiles!.OpenSubKey(sid);
                if (profile?.GetValue("ProfileImagePath") is not string path) continue;
                if (File.Exists(Path.Combine(Environment.ExpandEnvironmentVariables(path), "AppData", "Local", "Microsoft", "OneDrive", "OneDrive.exe")))
                {
                    perUser = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return (machineWide, perUser);
    }

    public string? MachineValue(string keyPath, string name)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(keyPath);
        return key?.GetValue(name) as string;
    }

    public IReadOnlyList<(string Name, string Category)> ConnectedNetworks()
    {
        var result = new List<(string, string)>();
        try
        {
            dynamic manager = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManager, throwOnError: true)!)!;
            // NLM_ENUM_NETWORK_CONNECTED = 1; NLM_NETWORK_CATEGORY: 0 public, 1 private, 2 domain.
            foreach (var network in manager.GetNetworks(1))
            {
                int category = network.GetCategory();
                result.Add(((string)network.GetName(), category switch { 1 => "private", 2 => "domain", _ => "public" }));
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
        return result;
    }

    public bool? InboundPortAllowed(int port)
    {
        try
        {
            dynamic policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
            var wanted = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var rule in policy.Rules)
            {
                // NET_FW_RULE_DIR_IN = 1, NET_FW_ACTION_ALLOW = 1, NET_FW_IP_PROTOCOL_TCP = 6.
                if (!(bool)rule.Enabled || (int)rule.Direction != 1 || (int)rule.Action != 1 || (int)rule.Protocol != 6) continue;
                var ports = rule.LocalPorts as string;
                if (ports is not null && ports.Split(',').Any(p => p.Trim() == wanted)) return true;
            }
            return false;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}

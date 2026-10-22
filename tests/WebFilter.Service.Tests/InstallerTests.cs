using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Model;
using WebFilter.Service.Policies;
using WebFilter.Service.Setup;

namespace WebFilter.Service.Tests;

/// <summary>
/// M7: the MSI source (installer/Package.wxs) agrees with the code it installs — service name, install folder, hub
/// port, native host keys, generated files, the command lines of its custom actions (run here through the real command)
/// and the data folder's ACL. Building the MSI is a separate Release build; installing it happens on the lab VM.
/// </summary>
public sealed class InstallerTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";
    private static readonly XNamespace Fw = "http://wixtoolset.org/schemas/v4/wxs/firewall";
    private static readonly XNamespace Util = "http://wixtoolset.org/schemas/v4/wxs/util";
    private static readonly XDocument Package = XDocument.Load(Path.Combine(RepoRoot(), "installer", "Package.wxs"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx"))) dir = dir.Parent ?? throw new InvalidOperationException("WebFilter.slnx not found");
        return dir.FullName;
    }

    private static XElement Single(string element, string id) => Package.Descendants(Wix + element).Single(e => (string?)e.Attribute("Id") == id);
    private static string? Attr(XElement element, string name) => (string?)element.Attribute(name);
    private static XElement Scheduled(string action) => Package.Descendants(Wix + "Custom").Single(c => Attr(c, "Action") == action);

    /// <summary>The command line a custom action runs, with properties expanded the way Windows Installer formats them.</summary>
    private static string[] CommandLine(string action, Dictionary<string, string> properties)
    {
        var command = Attr(Single("SetProperty", action), "Value")!;
        foreach (var (name, value) in properties) command = command.Replace($"[{name}]", value, StringComparison.Ordinal);
        Assert.DoesNotContain("[", command, StringComparison.Ordinal);
        return Split(command);
    }

    /// <summary>Splits like CommandLineToArgvW for arguments without escaped quotes ("" is an empty argument).</summary>
    private static string[] Split(string commandLine)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (c == ' ' && !quoted)
            {
                if (any) args.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else { current.Append(c); any = true; }
        }
        if (any) args.Add(current.ToString());
        return [.. args];
    }

    [Fact]
    public void The_service_is_installed_as_LocalSystem_starting_automatically_with_recovery()
    {
        var install = Package.Descendants(Wix + "ServiceInstall").Single();
        Assert.Equal(ServiceOptions.WindowsServiceName, Attr(install, "Name"));
        Assert.Equal("LocalSystem", Attr(install, "Account"));
        Assert.Equal("auto", Attr(install, "Start"));
        Assert.Equal("$(var.PayloadDir)WebFilter.Service.exe", Attr(install.Parent!.Element(Wix + "File")!, "Source"));
        var recovery = install.Element(Util + "ServiceConfig")!;
        Assert.All(new[] { "FirstFailureActionType", "SecondFailureActionType", "ThirdFailureActionType" }, a => Assert.Equal("restart", Attr(recovery, a)));

        var control = Package.Descendants(Wix + "ServiceControl").Single();
        Assert.Equal(ServiceOptions.WindowsServiceName, Attr(control, "Name"));
        Assert.Equal(("install", "both", "uninstall"), (Attr(control, "Start"), Attr(control, "Stop"), Attr(control, "Remove")));
    }

    [Fact]
    public void Files_go_to_the_folder_whose_native_host_the_service_trusts()
    {
        var folder = Package.Descendants(Wix + "StandardDirectory").Single(d => Attr(d, "Id") == "ProgramFiles64Folder").Element(Wix + "Directory")!;
        Assert.Equal(("INSTALLFOLDER", "WebFilter"), (Attr(folder, "Id"), Attr(folder, "Name")));
        var installFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WebFilter");
        Assert.Contains(Path.Combine(installFolder, InstallConfig.HostExeFileName), new ServiceOptions().TrustedClientPaths);
    }

    [Fact]
    public void The_native_wfp_engine_is_installed_next_to_the_service_and_built_with_the_msi()
    {
        // Authored as its own file rather than through the payload wildcard, so a build without the DLL fails.
        var source = "$(var.PayloadDir)" + Network.NativeWfpEngine.LibraryName;
        var file = Package.Descendants(Wix + "File").Single(f => Attr(f, "Source") == source);
        Assert.Equal("INSTALLFOLDER", Attr(file.Parent!, "Directory"));
        Assert.Contains(Package.Descendants(Wix + "Exclude"), e => Attr(e, "Files") == source);

        // The .wixproj builds the .vcxproj with Visual Studio's MSBuild and copies the DLL into the payload.
        var wixproj = XDocument.Load(Path.Combine(RepoRoot(), "installer", "WebFilter.Installer.wixproj"));
        var project = wixproj.Descendants("WfpNativeProject").Single().Value.Replace("$(MSBuildProjectDirectory)", Path.Combine(RepoRoot(), "installer"), StringComparison.Ordinal);
        Assert.True(File.Exists(project), project);
        Assert.Contains(wixproj.Descendants("Copy"), c => Attr(c, "DestinationFolder") == "$(PayloadDir)" && Attr(c, "SourceFiles") == "$(WfpNativeDll)");
    }

    [Fact]
    public void Chrome_and_Edge_find_the_native_host_through_the_same_HKLM_values_the_service_maintains()
    {
        var keys = Single("Component", "NativeHostRegistration").Elements(Wix + "RegistryKey").ToList();
        var manifest = "[INSTALLFOLDER]" + InstallConfig.HostManifestFileName;
        Assert.Equal(
            [$@"SOFTWARE\Google\Chrome\NativeMessagingHosts\{BrowserPolicies.HostName}", $@"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\{BrowserPolicies.HostName}"],
            keys.Select(k => Attr(k, "Key")));
        Assert.All(keys, k =>
        {
            Assert.Equal(("HKLM", "yes"), (Attr(k, "Root"), Attr(k, "ForceDeleteOnUninstall")));
            Assert.Equal(manifest, Attr(k.Element(Wix + "RegistryValue")!, "Value"));
        });

        // The service writes the same keys (and adopts the MSI's values rather than fighting them) when installed there.
        var installed = new ServiceOptions().NativeHostManifestPath;
        var policies = BrowserPolicies.Desired(new FamilyConfig { TimeZone = TimeZoneInfo.Utc }, "pc", new ExtensionIds(null, null), installed);
        foreach (var key in keys)
            Assert.Contains(policies, p => p.Hive == PolicyHive.Machine && p.KeyPath == Attr(key, "Key") && p.Name == "" && (string)p.Value == installed);
    }

    [Fact]
    public void Generated_files_are_not_shipped_and_are_removed_on_uninstall()
    {
        Assert.Contains(Package.Descendants(Wix + "Exclude"), e => Attr(e, "Files")!.EndsWith("appsettings*.json", StringComparison.Ordinal));
        var removed = Package.Descendants(Wix + "RemoveFile").Where(r => Attr(r, "On") == "uninstall").Select(r => Attr(r, "Name")).ToList();
        Assert.Contains(InstallConfig.AppSettingsFileName, removed);
        Assert.Contains(InstallConfig.HostManifestFileName, removed);
    }

    [Fact]
    public void The_hub_port_is_opened_for_private_networks_on_the_hub_only()
    {
        var component = Single("Component", "HubFirewall");
        Assert.Equal("ROLE~=\"hub\"", Attr(component, "Condition"));
        var rule = component.Element(Fw + "FirewallException")!;
        Assert.Equal(HubApi.DefaultPort.ToString(CultureInfo.InvariantCulture), Attr(rule, "Port"));
        Assert.Equal(("tcp", "private", "[#ServiceExe]"), (Attr(rule, "Protocol"), Attr(rule, "Profile"), Attr(rule, "Program")));
        Assert.Equal(HubApi.DefaultPort, new ServiceOptions().HubPort);
    }

    [Fact]
    public void The_notifier_is_installed_and_starts_at_logon_for_every_user_on_the_hub_only()
    {
        // PROTECTION-SPEC §10. Its own file (not through the payload wildcard), so the Run value can name it.
        var source = "$(var.PayloadDir)WebFilter.Notifier.exe";
        var file = Package.Descendants(Wix + "File").Single(f => Attr(f, "Source") == source);
        Assert.Equal(("NotifierExe", "INSTALLFOLDER"), (Attr(file, "Id"), Attr(file.Parent!, "Directory")));
        Assert.Contains(Package.Descendants(Wix + "Exclude"), e => Attr(e, "Files") == source);

        var autostart = Single("Component", "NotifierAutostart");
        Assert.Equal(Attr(Single("Component", "HubFirewall"), "Condition"), Attr(autostart, "Condition"));
        Assert.Equal("ROLE~=\"hub\"", Attr(autostart, "Condition"));
        var run = autostart.Element(Wix + "RegistryValue")!;
        Assert.Equal(("HKLM", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "string", "\"[INSTALLFOLDER]WebFilter.Notifier.exe\""),
            (Attr(run, "Root"), Attr(run, "Key"), Attr(run, "Type"), Attr(run, "Value")));
        var feature = Package.Descendants(Wix + "Feature").Single();
        Assert.All(new[] { "Notifier", "NotifierAutostart" }, id => Assert.Contains(feature.Elements(Wix + "ComponentRef"), r => Attr(r, "Id") == id));

        // A running notifier does not keep its files from being removed or replaced.
        var close = Package.Descendants(Util + "CloseApplication").Single();
        Assert.Equal(("WebFilter.Notifier.exe", "no"), (Attr(close, "Target"), Attr(close, "RebootPrompt")));
        Assert.NotNull(Attr(close, "TerminateProcess"));

        // The .wixproj publishes it into the payload, and the project builds an exe of that name.
        var wixproj = XDocument.Load(Path.Combine(RepoRoot(), "installer", "WebFilter.Installer.wixproj"));
        var project = wixproj.Descendants("PayloadProject").Select(p => Attr(p, "Include")!).Single(p => p.Contains("Notifier", StringComparison.Ordinal));
        var csproj = XDocument.Load(Path.Combine(RepoRoot(), "installer", project));
        Assert.Equal("WebFilter.Notifier.csproj", Path.GetFileName(project));
        Assert.Empty(csproj.Descendants("AssemblyName"));
        Assert.Equal("WinExe", csproj.Descendants("OutputType").Single().Value);
    }

    [Fact]
    public void Configure_runs_before_the_service_starts_and_its_command_line_works()
    {
        var scheduled = Scheduled("ConfigureService");
        Assert.Equal(("InstallFiles", "NOT REMOVE~=\"ALL\""), (Attr(scheduled, "After"), Attr(scheduled, "Condition")));
        var action = Single("CustomAction", "ConfigureService");
        Assert.Equal(("deferred", "no", "check"), (Attr(action, "Execute"), Attr(action, "Impersonate"), Attr(action, "Return")));

        var dir = Path.Combine(Path.GetTempPath(), "wf-msi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string edge = "edgeidedgeidedgeidedgeidedgeided";
            var args = CommandLine("ConfigureService", new()
            {
                ["INSTALLFOLDER"] = dir + @"\", ["ROLE"] = "agent", ["CHROME_EXTENSION_ID"] = "", ["EDGE_EXTENSION_ID"] = edge,
            });
            Assert.Equal((Path.Combine(dir, "WebFilter.Service.exe"), "configure"), (args[0], args[1]));

            // Installed, the command writes next to itself; here it is pointed at the temp folder.
            Assert.Equal(0, ConfigureCommand.Run([.. args[2..], "--dir", dir]));
            var options = new ServiceOptions();
            new ConfigurationBuilder().AddJsonFile(Path.Combine(dir, InstallConfig.AppSettingsFileName)).Build().GetSection("WebFilter").Bind(options);
            Assert.Equal(("agent", true, true, null, edge), (options.Role, options.ManageBrowserPolicies, options.ManageNetwork, options.ChromeExtensionId, options.EdgeExtensionId));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Pairing_runs_for_an_agent_given_both_values_and_cannot_fail_the_install()
    {
        var scheduled = Scheduled("PairWithHub");
        Assert.Contains("ROLE~=\"agent\" AND HUB AND PAIRCODE", Attr(scheduled, "Condition"), StringComparison.Ordinal);
        Assert.Equal("ConfigureService", Attr(scheduled, "After"));
        Assert.Equal("ignore", Attr(Single("CustomAction", "PairWithHub"), "Return"));
        Assert.Equal("yes", Attr(Single("CustomAction", "PairWithHub"), "HideTarget")); // the code must not reach the MSI log
        Assert.Equal("yes", Attr(Single("Property", "PAIRCODE"), "Hidden"));

        var args = CommandLine("PairWithHub", new() { ["INSTALLFOLDER"] = @"C:\Program Files\WebFilter\", ["HUB"] = "lab-hub.local:47800", ["PAIRCODE"] = "ABCD-EFGH-JKLM" });
        Assert.Equal(["pair", "--hub", "lab-hub.local:47800", "--code", "ABCD-EFGH-JKLM"], args[1..]);
    }

    [Fact]
    public void Cleanup_runs_on_uninstall_only_after_the_service_stopped_and_before_its_files_go()
    {
        var scheduled = Scheduled("CleanupMachine");
        // RemoveFiles comes after StopServices and DeleteServices in the standard sequence.
        Assert.Equal("RemoveFiles", Attr(scheduled, "Before"));
        Assert.Equal("REMOVE~=\"ALL\" AND NOT UPGRADINGPRODUCTCODE", Attr(scheduled, "Condition"));
        var action = Single("CustomAction", "CleanupMachine");
        Assert.Equal(("deferred", "no", "ignore"), (Attr(action, "Execute"), Attr(action, "Impersonate"), Attr(action, "Return")));
        Assert.Equal(["cleanup"], CommandLine("CleanupMachine", new() { ["INSTALLFOLDER"] = @"C:\Program Files\WebFilter\" })[1..]);
    }

    [Fact]
    public void The_data_folder_is_created_for_SYSTEM_and_Administrators_only()
    {
        var folder = Package.Descendants(Wix + "StandardDirectory").Single(d => Attr(d, "Id") == "CommonAppDataFolder").Element(Wix + "Directory")!;
        Assert.Equal(ServiceOptions.MachineDataRoot, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Attr(folder, "Name")!));

        var sddl = Attr(Single("Component", "DataFolder").Descendants(Wix + "PermissionEx").Single(), "Sddl")!;
        var descriptor = new RawSecurityDescriptor(sddl);
        Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected)); // nothing inherited from ProgramData (Users can read there)
        var trusted = new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) };
        Assert.All(descriptor.DiscretionaryAcl!.Cast<CommonAce>(), ace => Assert.Contains(ace.SecurityIdentifier, trusted));
    }
}

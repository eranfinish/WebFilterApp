using System.Text.Json;
using Microsoft.Extensions.Configuration;
using WebFilter.Service.Policies;
using WebFilter.Service.Setup;

namespace WebFilter.Service.Tests;

/// <summary>
/// M7: the files <c>WebFilter.Service configure</c> writes at install time — appsettings.json as the service binds it,
/// and the native messaging host manifest — and the command's argument handling as the installer calls it.
/// </summary>
public sealed class InstallConfigTests : IDisposable
{
    private const string ChromeId = "aaaabbbbccccddddeeeeffffgggghhhh", EdgeId = "edgeidedgeidedgeidedgeidedgeided";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-config-" + Guid.NewGuid().ToString("N"));

    public InstallConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ServiceOptions Bind(string appSettingsJson)
    {
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(appSettingsJson))).Build();
        var options = new ServiceOptions();
        configuration.GetSection("WebFilter").Bind(options);
        return options;
    }

    [Theory]
    [InlineData("hub")]
    [InlineData("agent")]
    public void The_service_reads_its_role_and_manages_browser_policies(string role)
    {
        var options = Bind(InstallConfig.AppSettings(role, new ExtensionIds(ChromeId, EdgeId)));

        Assert.Equal(role, options.Role);
        Assert.True(options.ManageBrowserPolicies);
        Assert.Equal(ChromeId, options.ChromeExtensionId);
        Assert.Equal(EdgeId, options.EdgeExtensionId);
    }

    [Fact]
    public void Unknown_extension_ids_are_left_out_and_everything_else_keeps_its_default()
    {
        var json = InstallConfig.AppSettings("agent", new ExtensionIds(null, null));
        var options = Bind(json);
        var defaults = new ServiceOptions();

        Assert.Null(options.ChromeExtensionId);
        Assert.Null(options.EdgeExtensionId);
        Assert.DoesNotContain("ExtensionId", json, StringComparison.Ordinal);
        Assert.Equal(defaults.DataDirectory, options.DataDirectory);
        Assert.Equal(defaults.TrustedClientPaths, options.TrustedClientPaths);
        Assert.Equal(defaults.HubPort, options.HubPort);
    }

    [Fact]
    public void The_host_manifest_admits_only_our_store_extensions()
    {
        using var doc = JsonDocument.Parse(InstallConfig.HostManifest(@"C:\Program Files\WebFilter\WebFilter.NativeHost.exe", new ExtensionIds(ChromeId, EdgeId)));
        var root = doc.RootElement;

        Assert.Equal(BrowserPolicies.HostName, root.GetProperty("name").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());
        Assert.Equal(@"C:\Program Files\WebFilter\WebFilter.NativeHost.exe", root.GetProperty("path").GetString());
        Assert.Equal([$"chrome-extension://{ChromeId}/", $"chrome-extension://{EdgeId}/"], root.GetProperty("allowed_origins").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Without_store_ids_the_host_admits_no_extension()
    {
        using var doc = JsonDocument.Parse(InstallConfig.HostManifest(@"C:\x\WebFilter.NativeHost.exe", new ExtensionIds(null, null)));
        Assert.Empty(doc.RootElement.GetProperty("allowed_origins").EnumerateArray());
    }

    [Fact]
    public void The_installed_paths_are_the_ones_the_service_trusts()
    {
        var installFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WebFilter");
        var defaults = new ServiceOptions();

        Assert.Equal(Path.Combine(installFolder, InstallConfig.HostManifestFileName), defaults.NativeHostManifestPath);
        Assert.Contains(Path.Combine(installFolder, InstallConfig.HostExeFileName), defaults.TrustedClientPaths);
    }

    [Theory]
    [InlineData("hub", "hub")]
    [InlineData(" Agent ", "agent")]
    [InlineData("HUB", "hub")]
    [InlineData("parent", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Roles_are_hub_or_agent(string? input, string? expected) => Assert.Equal(expected, InstallConfig.NormalizeRole(input));

    [Theory]
    [InlineData(ChromeId, true)]
    [InlineData("abcdefghijklmnopabcdefghijklmnop", true)]
    [InlineData("abcdefghijklmnopabcdefghijklmnoq", false)] // q is outside a-p
    [InlineData("ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOP", false)]
    [InlineData("abcdefghijklmnop", false)]
    [InlineData("abcdefghijklmnopabcdefghijklmno\"", false)]
    public void Extension_ids_are_32_letters_a_to_p(string id, bool valid) => Assert.Equal(valid, InstallConfig.IsExtensionId(id));

    [Fact]
    public void Configure_writes_both_files_as_the_installer_calls_it()
    {
        // Unset installer properties arrive as "".
        var exit = ConfigureCommand.Run(["--role", "agent", "--chrome-extension-id", "", "--edge-extension-id", EdgeId.ToUpperInvariant(), "--dir", _dir]);

        Assert.Equal(0, exit);
        var options = Bind(File.ReadAllText(Path.Combine(_dir, InstallConfig.AppSettingsFileName)));
        Assert.Equal("agent", options.Role);
        Assert.Null(options.ChromeExtensionId);
        Assert.Equal(EdgeId, options.EdgeExtensionId);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, InstallConfig.HostManifestFileName)));
        Assert.Equal(Path.Combine(_dir, InstallConfig.HostExeFileName), manifest.RootElement.GetProperty("path").GetString());
        Assert.Equal([$"chrome-extension://{EdgeId}/"], manifest.RootElement.GetProperty("allowed_origins").EnumerateArray().Select(e => e.GetString()));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Configure_replaces_earlier_files_on_repair()
    {
        Assert.Equal(0, ConfigureCommand.Run(["--role", "hub", "--dir", _dir]));
        Assert.Equal(0, ConfigureCommand.Run(["--role", "hub", "--chrome-extension-id", ChromeId, "--dir", _dir]));

        Assert.Equal(ChromeId, Bind(File.ReadAllText(Path.Combine(_dir, InstallConfig.AppSettingsFileName))).ChromeExtensionId);
    }

    [Fact]
    public void Configure_treats_a_missing_value_as_not_set()
    {
        Assert.Equal(0, ConfigureCommand.Run(["--role", "hub", "--chrome-extension-id", "--edge-extension-id", EdgeId, "--dir", _dir]));

        var options = Bind(File.ReadAllText(Path.Combine(_dir, InstallConfig.AppSettingsFileName)));
        Assert.Null(options.ChromeExtensionId);
        Assert.Equal(EdgeId, options.EdgeExtensionId);
    }

    [Theory]
    [InlineData("--role", "parent")]
    [InlineData("--chrome-extension-id", ChromeId)]
    [InlineData("--role", "hub", "--chrome-extension-id", "abc")]
    [InlineData("--role", "hub", "--edge-extension-id", "ABCDEFGHIJKLMNOPABCDEFGHIJKLMNOQ")]
    public void Configure_rejects_bad_arguments_and_writes_nothing(params string[] args)
    {
        Assert.Equal(2, ConfigureCommand.Run([.. args, "--dir", _dir]));
        Assert.Empty(Directory.GetFiles(_dir));
    }
}

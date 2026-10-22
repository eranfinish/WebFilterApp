using WebFilter.Contracts.Admin;
using WebFilter.Service.Setup;

namespace WebFilter.Service.Tests;

public sealed class MachineChecksTests
{
    private readonly FakeMachineProbe _probe = new();

    private static ServiceOptions Installed() => new()
    {
        ManageBrowserPolicies = true, ChromeExtensionId = "abcdefghijklmnopabcdefghijklmnop", EdgeExtensionId = "ponmlkjihgfedcbaponmlkjihgfedcba", HubPort = 47800,
    };

    [Fact]
    public void An_installed_hub_on_a_private_network_passes_every_check()
    {
        var checks = MachineChecks.Run(Installed(), isHub: true, _probe);

        Assert.Equal(
        [
            new CheckResult("service", "ok"), new CheckResult("browsers", "ok", "edge,chrome"), new CheckResult("browser_policies", "ok"),
            new CheckResult("extension", "ok"), new CheckResult("onedrive", "ok"), new CheckResult("network", "ok"), new CheckResult("hub_port", "ok", "47800"),
        ], checks);
    }

    [Fact]
    public void An_agent_is_not_asked_about_networks_or_the_hub_port()
    {
        _probe.Networks.Add(("Cafe", "public"));

        Assert.Equal(["service", "browsers", "browser_policies", "extension", "onedrive"], MachineChecks.Run(Installed(), isHub: false, _probe).Select(c => c.Id));
    }

    [Theory]
    [InlineData(true, false, "ok", null)]
    [InlineData(true, true, "ok", null)]
    [InlineData(false, true, "warn", "OneDriveSetup.exe /allusers")]
    [InlineData(false, false, "ok", "none")]
    public void OneDrive_must_be_installed_machine_wide_for_restricted_accounts_to_use_it(bool machineWide, bool perUser, string state, string? detail)
    {
        _probe.OneDrive = (machineWide, perUser);

        Assert.Equal(new CheckResult("onedrive", state, detail), Check(MachineChecks.Run(Installed(), isHub: false, _probe), "onedrive"));
    }

    [Fact]
    public void A_development_run_is_flagged_and_the_unpublished_extension_is_a_warning()
    {
        _probe.LocalSystem = false;

        var checks = MachineChecks.Run(new ServiceOptions { HubPort = 47800 }, isHub: true, _probe).ToDictionary(c => c.Id, c => c.State);

        Assert.Equal(("warn", "warn", "warn"), (checks["service"], checks["browser_policies"], checks["extension"]));
    }

    [Fact]
    public void A_public_network_on_the_hub_is_named_because_agents_cannot_reach_it()
    {
        _probe.Networks.Add(("Network 3", "public"));

        Assert.Equal(new CheckResult("network", "warn", "Network 3"), Check(MachineChecks.Run(Installed(), true, _probe), "network"));
    }

    [Fact]
    public void No_browser_no_network_and_a_missing_policy_fail()
    {
        _probe.Browsers.Clear();
        _probe.Networks.Clear();
        _probe.Values.Clear();

        var checks = MachineChecks.Run(Installed(), true, _probe).ToDictionary(c => c.Id, c => c.State);

        Assert.Equal(("fail", "fail", "fail"), (checks["browsers"], checks["network"], checks["extension"]));
    }

    [Fact]
    public void A_closed_port_is_a_warning_and_an_unreadable_firewall_is_left_out()
    {
        _probe.PortOpen = false;
        Assert.Equal(new CheckResult("hub_port", "warn", "47800"), Check(MachineChecks.Run(Installed(), true, _probe), "hub_port"));

        _probe.PortOpen = null;
        Assert.DoesNotContain(MachineChecks.Run(Installed(), true, _probe), c => c.Id == "hub_port");
    }

    [Fact]
    public void The_windows_probe_only_reads()
    {
        var probe = new WindowsMachineProbe();

        Assert.False(probe.RunsAsLocalSystem);
        Assert.All(probe.InstalledBrowsers(), b => Assert.Contains(b, new[] { "edge", "chrome" }));
        Assert.All(probe.ConnectedNetworks(), n => Assert.Contains(n.Category, new[] { "public", "private", "domain" }));
        _ = probe.InboundPortAllowed(47800);
        _ = probe.OneDriveInstalls();
        Assert.Null(probe.MachineValue(@"SOFTWARE\WebFilter.Tests.NoSuchKey", "1"));
    }

    private static CheckResult Check(IEnumerable<CheckResult> checks, string id) => checks.Single(c => c.Id == id);

    private sealed class FakeMachineProbe : IMachineProbe
    {
        public bool LocalSystem { get; set; } = true;

        public List<string> Browsers { get; } = ["edge", "chrome"];

        public Dictionary<(string, string), string> Values { get; } = new()
        {
            [(@"SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist", "1")] = "abcdefghijklmnopabcdefghijklmnop;https://clients2.google.com/service/update2/crx",
        };

        public List<(string Name, string Category)> Networks { get; } = [("Home", "private")];

        public bool? PortOpen { get; set; } = true;

        public (bool MachineWide, bool PerUser) OneDrive { get; set; } = (true, false);

        public bool RunsAsLocalSystem => LocalSystem;

        public (bool MachineWide, bool PerUser) OneDriveInstalls() => OneDrive;

        public IReadOnlyList<string> InstalledBrowsers() => Browsers;

        public string? MachineValue(string keyPath, string name) => Values.GetValueOrDefault((keyPath, name));

        public IReadOnlyList<(string Name, string Category)> ConnectedNetworks() => Networks;

        public bool? InboundPortAllowed(int port) => PortOpen;
    }
}

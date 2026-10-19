using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Contracts.Hub;
using WebFilter.Core.Engine;
using WebFilter.Service.Hub;
using WebFilter.Service.Mapping;
using WebFilter.Service.Policies;

namespace WebFilter.Service.Tests;

/// <summary>
/// Found in the lab (M7): an installed agent that had not yet synced with its hub stopped the whole service, because the
/// policy pass read settings that did not exist yet; and an unreachable hub crashed <c>pair</c> instead of reporting.
/// </summary>
public sealed class PolicyServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-policysvc-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task An_agent_without_settings_keeps_running_and_applies_policies_once_they_arrive()
    {
        var options = new ServiceOptions
        {
            Role = "agent", DeviceId = "pc-kids", ManageBrowserPolicies = true, PolicyInterval = TimeSpan.FromMinutes(5),
            DataDirectory = Path.Combine(_dir, "data"), ProfileDirectory = Path.Combine(_dir, "profiles"),
        };
        using var family = new FamilyService(Options.Create(options), TimeProvider.System, NullLogger<FamilyService>.Instance);
        family.Start();
        var store = new MemoryPolicyStore();
        using var service = new PolicyService(family, Options.Create(options), NullLogger<PolicyService>.Instance, store);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Passed += ok => (ok ? applied : waiting).TrySetResult();

        await service.StartAsync(CancellationToken.None);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(service.ExecuteTask!.IsCompleted);
        Assert.Empty(store.All);

        family.ApplyRemoteSnapshot(new HubCore(Fixture()).Snapshot());
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, store.Machine(@"SOFTWARE\Policies\Microsoft\Edge", "NativeMessagingUserLevelHosts"));
        Assert.Equal(1, store.User("S-1-5-21-2000-1001", @"SOFTWARE\Policies\Microsoft\Edge", "InPrivateModeAvailability"));

        await service.StopAsync(CancellationToken.None);
        Assert.False(service.ExecuteTask.IsFaulted);
    }

    [Fact]
    public async Task Pairing_with_a_hub_that_never_answers_reports_a_clear_failure()
    {
        // Accepts the connection and then says nothing, like a hub behind a firewall that drops traffic.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = listener.AcceptTcpClientAsync();
        var error = new StringWriter();
        var previous = Console.Error;
        int exit;
        try
        {
            Console.SetError(error);
            exit = await PairCommand.RunAsync(["--hub", $"127.0.0.1:{port}", "--code", "AAAA-BBBB-CCCC", "--data", Path.Combine(_dir, "pair")]);
        }
        finally
        {
            Console.SetError(previous);
            listener.Stop();
            (await accepted).Dispose();
        }

        Assert.Equal(1, exit);
        Assert.Contains("Pairing failed", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Private", error.ToString(), StringComparison.Ordinal);
        Assert.Null(HubLink.Load(Path.Combine(_dir, "pair")));
    }

    private static Core.Model.FamilyConfig Fixture()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "WebFilter.slnx"))) root = root.Parent!;
        return BundleMapper.ToConfig(FamilyBundleJson.Deserialize(File.ReadAllText(Path.Combine(root.FullName, "tests", "simulation", "fixtures", "family-base.json"))));
    }
}

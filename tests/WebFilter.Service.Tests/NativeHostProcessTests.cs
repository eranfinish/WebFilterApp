using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Contracts.Agent;
using WebFilter.Service.Pipes;

namespace WebFilter.Service.Tests;

/// <summary>
/// Runs the real native host executable (Simulation build, which accepts pipe/profile overrides) the way a browser
/// does: stdin/stdout with length-prefixed JSON.
/// </summary>
public sealed class NativeHostProcessTests : IAsyncLifetime
{
    private static readonly string UserSid = WindowsIdentity.GetCurrent().User!.Value;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-host-" + Guid.NewGuid().ToString("N"));
    private readonly string _pipe = "WebFilter.Test.Agent." + Guid.NewGuid().ToString("N")[..8];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private static string HostExe()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx")))
                return Path.Combine(dir.FullName, "src", "WebFilter.NativeHost", "bin", "Simulation", "net10.0-windows", "WebFilter.NativeHost.exe");
        throw new InvalidOperationException("WebFilter.slnx not found");
    }

    private Process StartHost(string pipeName)
    {
        var start = new ProcessStartInfo(HostExe(), "chrome-extension://abcdefghijklmnopabcdefghijklmnop/")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.Environment["WEBFILTER_AGENT_PIPE"] = pipeName;
        start.Environment["WEBFILTER_PROFILE_DIR"] = Path.Combine(_dir, "profiles");
        return Process.Start(start)!;
    }

    private static async Task<AgentMessage> AskAsync(Process host, AgentMessage message)
    {
        await AgentProtocol.WriteAsync(host.StandardInput.BaseStream, message);
        var reply = await AgentProtocol.ReadAsync(host.StandardOutput.BaseStream).WaitAsync(TimeSpan.FromSeconds(15));
        return reply ?? throw new EndOfStreamException("The host closed stdout");
    }

    [Fact]
    public async Task Without_a_service_the_host_answers_itself_and_admins_are_unrestricted()
    {
        using var host = StartHost(_pipe); // nobody listens on this pipe
        try
        {
            var hello = Assert.IsType<HelloResponse>(await AskAsync(host, new HelloRequest("0.0.1", "edge") { Id = "1" }));
            Assert.Equal("1", hello.Id);
            Assert.Equal("host", hello.Source);
            Assert.False(hello.ServiceAvailable);
            Assert.Equal(UserSid, hello.User.AccountId);
            // The test account is an administrator (elevated or not), so it must not be locked out.
            Assert.Equal(hello.User.IsAdmin ? "unrestricted" : "allowlist", hello.Mode);
            var access = Assert.IsType<RequestAccessResponse>(await AskAsync(host, new RequestAccessRequest("url", "https://x.example/", null, null)));
            Assert.Equal("unavailable", access.Result);
        }
        finally
        {
            host.Kill();
        }
    }

    [Fact]
    public async Task With_the_service_the_host_relays_and_the_service_sees_the_browser_user()
    {
        var bundle = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(HostExe())!, "..", "..", "..", "..", "..", "tests", "simulation", "fixtures", "family-base.json")))!;
        foreach (var account in bundle["devices"]!.AsArray().First(d => (string)d!["id"]! == "pc-kids")!["accounts"]!.AsArray())
            if ((string?)account!["member"] == "noa") account["accountId"] = UserSid;
        var bundlePath = Path.Combine(_dir, "bootstrap.json");
        File.WriteAllText(bundlePath, bundle.ToJsonString());
        var options = new ServiceOptions
        {
            DeviceId = "pc-kids",
            DataDirectory = Path.Combine(_dir, "data"),
            ProfileDirectory = Path.Combine(_dir, "profiles"),
            AgentPipeName = _pipe,
            AdminPipeName = _pipe + ".admin",
            TrustedClientPaths = [HostExe()],
            BootstrapBundlePath = bundlePath,
        };
        using var family = new FamilyService(Options.Create(options), TimeProvider.System, NullLogger<FamilyService>.Instance);
        family.Start();
        var server = new AgentPipeServer(family, Options.Create(options), NullLogger<AgentPipeServer>.Instance);
        await server.StartAsync(CancellationToken.None);
        using var host = StartHost(_pipe);
        try
        {
            var hello = Assert.IsType<HelloResponse>(await AskAsync(host, new HelloRequest("0.0.1", "edge") { Id = "h" }));
            Assert.Equal("service", hello.Source);
            Assert.Equal(UserSid, hello.User.AccountId);
            Assert.Equal("child", hello.User.Role);
            Assert.True(File.Exists(Path.Combine(options.ProfileDirectory, UserSid + ".json")), "the service writes a profile cache for each account");
        }
        finally
        {
            host.Kill();
            await server.StopAsync(CancellationToken.None);
        }
    }
}

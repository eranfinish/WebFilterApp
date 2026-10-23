using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
/// policy pass read settings that did not exist yet; an unreachable hub crashed <c>pair</c> instead of reporting; and a child
/// who logged on waited up to a minute (<see cref="ServiceOptions.PolicyInterval"/>) for their own policies, because they
/// can be written only once Windows has loaded the child's registry hive.
/// </summary>
public sealed class PolicyServiceTests : IDisposable
{
    /// <summary>noa: a restricted child on pc-kids in the fixture.</summary>
    private const string Noa = "S-1-5-21-2000-1001";
    private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-policysvc-" + Guid.NewGuid().ToString("N"));
    private readonly SessionEvents _sessions = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ServiceOptions Settings(TimeSpan? policyInterval = null, TimeSpan? logonRecheck = null) => new()
    {
        Role = "agent", DeviceId = "pc-kids", ManageBrowserPolicies = true,
        PolicyInterval = policyInterval ?? TimeSpan.FromMinutes(5), LogonRecheck = logonRecheck ?? TimeSpan.FromHours(1),
        DataDirectory = Path.Combine(_dir, "data"), ProfileDirectory = Path.Combine(_dir, "profiles"),
    };

    /// <summary>An agent that already has the family settings from its hub.</summary>
    private static FamilyService Synced(ServiceOptions options)
    {
        var family = new FamilyService(Options.Create(options), TimeProvider.System, NullLogger<FamilyService>.Instance);
        family.Start();
        family.ApplyRemoteSnapshot(new HubCore(Fixture()).Snapshot());
        return family;
    }

    private PolicyService Policies(FamilyService family, ServiceOptions options, IPolicyStore store) =>
        new(family, _sessions, Options.Create(options), NullLogger<PolicyService>.Instance, store);

    /// <summary>Counts passes; <see cref="Next"/> waits for the next one not waited for yet.</summary>
    private sealed class Passes
    {
        private readonly SemaphoreSlim _done = new(0);

        /// <param name="onPass">Runs on the policy loop after each pass (number from 1, applied), before it waits again.</param>
        public Passes(PolicyService service, Action<int, bool>? onPass = null)
        {
            var count = 0;
            service.Passed += applied =>
            {
                onPass?.Invoke(++count, applied);
                _done.Release();
            };
        }

        public async Task Next() => Assert.True(await _done.WaitAsync(Wait), "no policy pass");
    }

    [Fact]
    public async Task An_agent_without_settings_keeps_running_and_applies_policies_once_they_arrive()
    {
        var options = Settings();
        using var family = new FamilyService(Options.Create(options), TimeProvider.System, NullLogger<FamilyService>.Instance);
        family.Start();
        var store = new MemoryPolicyStore();
        using var service = Policies(family, options, store);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Passed += ok => (ok ? applied : waiting).TrySetResult();

        await service.StartAsync(CancellationToken.None);
        await waiting.Task.WaitAsync(Wait);
        Assert.False(service.ExecuteTask!.IsCompleted);
        Assert.Empty(store.All);

        family.ApplyRemoteSnapshot(new HubCore(Fixture()).Snapshot());
        await applied.Task.WaitAsync(Wait);
        Assert.Equal(0, store.Machine(Edge, "NativeMessagingUserLevelHosts"));
        Assert.Equal(1, store.User(Noa, Edge, "InPrivateModeAvailability"));

        await service.StopAsync(CancellationToken.None);
        Assert.False(service.ExecuteTask.IsFaulted);
    }

    [Fact]
    public async Task A_child_who_logs_on_gets_their_policies_at_once_not_on_the_next_pass()
    {
        var options = Settings(policyInterval: TimeSpan.FromHours(1));
        using var family = Synced(options);
        var store = new MemoryPolicyStore();
        store.Unloaded.Add(Noa); // noa is not logged on
        using var service = Policies(family, options, store);
        var passes = new Passes(service);

        await service.StartAsync(CancellationToken.None);
        await passes.Next();
        Assert.Equal(0, store.Machine(Edge, "NativeMessagingUserLevelHosts"));
        Assert.Empty(store.ValuesOf(Noa));

        // noa logs on: Windows loads her hive, then reports the logon to the service. The next timed pass is an hour away.
        store.Unloaded.Remove(Noa);
        _sessions.OnSessionChange(SessionChangeReason.SessionLogon, 2);
        await passes.Next();

        await service.StopAsync(CancellationToken.None);
        Assert.Equal(1, store.User(Noa, Edge, "InPrivateModeAvailability"));
        Assert.Equal(0, store.User(Noa, Edge, "BrowserGuestModeEnabled"));
    }

    [Fact]
    public async Task A_hive_loaded_just_after_the_logon_report_is_covered_moments_later()
    {
        var options = Settings(policyInterval: TimeSpan.FromHours(1), logonRecheck: TimeSpan.FromMilliseconds(50));
        using var family = Synced(options);
        var store = new MemoryPolicyStore();
        store.Unloaded.Add(Noa);
        using var service = Policies(family, options, store);
        var writtenByLogonPass = true;
        var passes = new Passes(service, (pass, _) =>
        {
            if (pass != 2) return;
            // The pass the logon woke found no hive yet; Windows finishes loading it now.
            writtenByLogonPass = store.ValuesOf(Noa).Any();
            store.Unloaded.Remove(Noa);
        });

        await service.StartAsync(CancellationToken.None);
        await passes.Next();
        _sessions.OnSessionChange(SessionChangeReason.SessionLogon, 2);
        await passes.Next();
        await passes.Next();

        await service.StopAsync(CancellationToken.None);
        Assert.False(writtenByLogonPass);
        Assert.Equal(1, store.User(Noa, Edge, "InPrivateModeAvailability"));
    }

    [Fact]
    public async Task After_the_rechecks_of_a_logon_the_service_goes_back_to_its_usual_interval()
    {
        var options = Settings(policyInterval: TimeSpan.FromHours(1), logonRecheck: TimeSpan.FromMilliseconds(20));
        using var family = Synced(options);
        using var service = Policies(family, options, new MemoryPolicyStore());
        var count = 0;
        var passes = new Passes(service, (pass, _) => Volatile.Write(ref count, pass));

        await service.StartAsync(CancellationToken.None);
        await passes.Next();
        _sessions.OnSessionChange(SessionChangeReason.SessionLogon, 2);
        for (var i = 0; i < 1 + PolicyService.LogonRechecks; i++) await passes.Next();
        await Task.Delay(300);

        await service.StopAsync(CancellationToken.None);
        // The start, the logon, its rechecks — and nothing more within the hour.
        Assert.Equal(2 + PolicyService.LogonRechecks, Volatile.Read(ref count));
    }

    [Fact]
    public void Only_a_logon_counts_as_one()
    {
        var events = new SessionEvents();
        var logons = new List<int>();
        events.LoggedOn += logons.Add;

        foreach (var reason in Enum.GetValues<SessionChangeReason>()) events.OnSessionChange(reason, (int)reason);

        Assert.Equal([(int)SessionChangeReason.SessionLogon], logons);
    }

    [Fact]
    public void As_a_Windows_service_the_lifetime_asks_Windows_for_session_changes_and_passes_logons_on()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSessionEvents(windowsService: true);
        using var host = builder.Build();
        var lifetime = Assert.IsType<SessionAwareServiceLifetime>(host.Services.GetRequiredService<IHostLifetime>());
        Assert.True(lifetime.CanHandleSessionChangeEvent); // SERVICE_ACCEPT_SESSIONCHANGE
        Assert.True(lifetime.CanShutdown);
        var logons = new List<int>();
        host.Services.GetRequiredService<SessionEvents>().LoggedOn += logons.Add;

        // What ServiceBase does when the service control manager sends SERVICE_CONTROL_SESSIONCHANGE with WTS_SESSION_LOGON.
        var change = (SessionChangeDescription)Activator.CreateInstance(typeof(SessionChangeDescription), BindingFlags.NonPublic | BindingFlags.Instance,
            null, [SessionChangeReason.SessionLogon, 3], null)!;
        typeof(SessionAwareServiceLifetime).GetMethod("OnSessionChange", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(lifetime, [change]);

        Assert.Equal([3], logons);
    }

    [Fact]
    public void Outside_a_Windows_service_the_lifetime_is_left_alone()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        var lifetime = builder.Services.Single(s => s.ServiceType == typeof(IHostLifetime));

        builder.Services.AddSessionEvents();

        Assert.Same(lifetime, builder.Services.Single(s => s.ServiceType == typeof(IHostLifetime)));
        Assert.Contains(builder.Services, s => s.ServiceType == typeof(SessionEvents));
    }

    /// <summary>PROTECTION-SPEC §10: a policy someone changed is restored and reported as tamper, once per hour for the same account.</summary>
    [Fact]
    public async Task A_changed_policy_is_restored_and_reported_to_the_parents()
    {
        var options = Settings(policyInterval: TimeSpan.FromHours(1));
        using var family = Synced(options);
        var store = new MemoryPolicyStore();
        using var service = Policies(family, options, store);
        var passes = new Passes(service);

        await service.StartAsync(CancellationToken.None);
        await passes.Next();
        store.Set(PolicyHive.User, Noa, Edge, "InPrivateModeAvailability", 0);
        _sessions.OnSessionChange(SessionChangeReason.SessionLogon, 2);
        await passes.Next();
        store.Set(PolicyHive.User, Noa, Edge, "InPrivateModeAvailability", 0);
        _sessions.OnSessionChange(SessionChangeReason.SessionLogon, 2);
        await passes.Next();
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, store.User(Noa, Edge, "InPrivateModeAvailability"));
        var tamper = Assert.Single(family.WithAgent((agent, _) => agent.LocalEvents.Where(e => e.Type == EventType.Tamper).ToList()));
        Assert.Equal((TamperKinds.PolicyRepaired, "noa", "pc-kids"), (tamper.Detail, tamper.MemberId, tamper.DeviceId));
    }

    /// <summary>
    /// PROTECTION-SPEC §13: a parent who removes the baseline's SafeSearch from their own account gets it back (the switch
    /// is in the admin app's settings), and nobody is told — it is not a child working around the filter.
    /// </summary>
    [Fact]
    public async Task A_parents_own_SafeSearch_is_restored_without_a_tamper_report()
    {
        const string Dana = "S-1-5-21-2000-1003", Savta = "S-1-5-21-2000-1004";
        var options = Settings(policyInterval: TimeSpan.FromHours(1));
        using var family = Synced(options);
        var store = new MemoryPolicyStore();
        using var service = Policies(family, options, store);
        var passes = new Passes(service);

        await service.StartAsync(CancellationToken.None);
        await passes.Next();
        Assert.Equal(1, store.User(Dana, Edge, "ForceGoogleSafeSearch"));
        store.Delete(PolicyHive.User, Dana, Edge, "ForceGoogleSafeSearch");
        store.Set(PolicyHive.User, Savta, Edge, "ForceBingSafeSearch", 0);
        _sessions.OnSessionChange(SessionChangeReason.SessionLogon, 2);
        await passes.Next();
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(1, store.User(Dana, Edge, "ForceGoogleSafeSearch"));
        Assert.Equal(1, store.User(Savta, Edge, "ForceBingSafeSearch"));
        Assert.Empty(family.WithAgent((agent, _) => agent.LocalEvents.Where(e => e.Type == EventType.Tamper).ToList()));
    }

    [Fact]
    public async Task A_pass_that_fails_is_retried_at_the_next_logon_and_does_not_end_the_component()
    {
        // The registry can refuse a write (a hive being unloaded, a locked key): PolicyService logs it and tries again,
        // and its ExecuteAsync keeps running, so the host never sees a failed component.
        var options = Settings(policyInterval: TimeSpan.FromHours(1));
        using var family = Synced(options);
        var store = new FailsOnce(new MemoryPolicyStore());
        using var service = Policies(family, options, store);
        var results = new List<bool>();
        var passes = new Passes(service, (_, applied) => results.Add(applied));

        await service.StartAsync(CancellationToken.None);
        await passes.Next();
        _sessions.OnSessionChange(SessionChangeReason.SessionLogon, 2);
        await passes.Next();

        Assert.False(service.ExecuteTask!.IsCompleted);
        await service.StopAsync(CancellationToken.None);
        Assert.False(service.ExecuteTask.IsFaulted);
        Assert.Equal([false, true], results);
        Assert.Equal(1, store.Inner.User(Noa, Edge, "InPrivateModeAvailability"));
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

    /// <summary>The first write fails, as the real registry can.</summary>
    private sealed class FailsOnce(MemoryPolicyStore inner) : IPolicyStore
    {
        private bool _failed;

        public MemoryPolicyStore Inner => inner;
        public bool IsAvailable(PolicyHive hive, string? userSid) => inner.IsAvailable(hive, userSid);
        public object? Get(PolicyHive hive, string? userSid, string keyPath, string name) => inner.Get(hive, userSid, keyPath, name);
        public void Delete(PolicyHive hive, string? userSid, string keyPath, string name) => inner.Delete(hive, userSid, keyPath, name);

        public void Set(PolicyHive hive, string? userSid, string keyPath, string name, object value)
        {
            if (!_failed)
            {
                _failed = true;
                throw new UnauthorizedAccessException("Access to the registry key is denied.");
            }
            inner.Set(hive, userSid, keyPath, name, value);
        }
    }
}

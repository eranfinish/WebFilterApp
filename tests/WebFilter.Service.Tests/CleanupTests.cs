using WebFilter.Core.Engine;
using WebFilter.Service.Hub;
using WebFilter.Service.Network;
using WebFilter.Service.Policies;
using WebFilter.Service.Setup;

namespace WebFilter.Service.Tests;

/// <summary>
/// M7 uninstall (<c>WebFilter.Service cleanup</c>) against the in-memory registry, a fake hub link and a temp data folder:
/// every policy value the service wrote goes, including those of users who are not logged on (their hive is loaded);
/// values written by others stay; WFP filters go; the hub is told while the pairing is still there, then the data folder
/// goes; failures are reported, not fatal, and a hub that is off or silent does not hold the uninstall up. The real
/// registry, hives and WFP are exercised only on the lab VM; the real hub link in <see cref="HubSyncTests"/>.
/// </summary>
public sealed class CleanupTests : IDisposable
{
    private const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
    private const string Noa = "S-1-5-21-2000-1001", Yoav = "S-1-5-21-2000-1002", Visitor = "S-1-5-21-2000-1005";
    private static readonly TimeSpan HubTimeout = TimeSpan.FromMilliseconds(300);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wf-cleanup-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryPolicyStore _store = new();
    private readonly FakeHives _hives;
    private readonly FakeHubLink _hub;
    private readonly StringWriter _log = new();

    public CleanupTests()
    {
        _hives = new FakeHives(_store);
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateDirectory(Path.Combine(_root, "profiles"));
        File.WriteAllText(Path.Combine(_root, "data", "family.db"), "x");
        File.WriteAllText(Path.Combine(_root, "data", "hub-client.pfx.dpapi"), "key");
        File.WriteAllText(Path.Combine(_root, "profiles", Noa + ".json"), "{}");
        _hub = new FakeHubLink(Path.Combine(_root, "data", "hub-client.pfx.dpapi"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private string StatePath => Path.Combine(_root, "data", PolicyService.StateFileName);

    /// <summary>The installed service applied the policies of the fixture family (with every user logged on).</summary>
    private void ServiceWrotePolicies()
    {
        var ids = new ExtensionIds("chromeidchromeidchromeidchromeid", "edgeidedgeidedgeidedgeidedgeided");
        new PolicyManager(_store, StatePath).Apply(BrowserPolicies.Desired(PolicyTests.Fixture(), "pc-kids", ids, @"C:\Program Files\WebFilter\com.webfilter.host.json"));
    }

    private MachineCleanup.Result Cleanup(IWfpEngine? wfp = null) =>
        new MachineCleanup(_store, _hives, wfp, _hub, _log) { HubTimeout = HubTimeout }.Run(_root, StatePath);

    [Fact]
    public void Every_value_the_service_wrote_is_removed_and_others_stay()
    {
        _store.Set(PolicyHive.Machine, null, Chrome, "SomeoneElsesPolicy", 1);
        _store.Set(PolicyHive.User, Yoav, Chrome, "SomeoneElsesUserPolicy", 1);
        ServiceWrotePolicies();
        Assert.NotEmpty(_store.ValuesOf(Noa));

        var result = Cleanup();

        Assert.True(result.Complete);
        Assert.True(result.PoliciesRemoved > 20);
        Assert.Equal(["SomeoneElsesPolicy", "SomeoneElsesUserPolicy"], _store.All.Select(v => v.Name).Order());
        Assert.Empty(_hives.Loaded); // everyone was logged on
    }

    [Fact]
    public void Users_who_are_not_logged_on_have_their_hive_loaded_and_cleaned()
    {
        ServiceWrotePolicies();
        _store.Unloaded.Add(Noa);
        _store.Unloaded.Add(Visitor);

        var result = Cleanup();

        Assert.True(result.Complete);
        Assert.Empty(_store.ValuesOf(Noa));
        Assert.Empty(_store.ValuesOf(Visitor));
        Assert.Empty(_store.ValuesOf(Yoav));
        Assert.Equal([Noa, Visitor], _hives.Loaded.Order()); // once each, and only the users who were not logged on
    }

    [Fact]
    public void A_hive_that_cannot_be_loaded_is_reported_and_the_rest_still_happens()
    {
        ServiceWrotePolicies();
        _store.Unloaded.Add(Noa);
        _hives.Broken.Add(Noa);

        var result = Cleanup();

        Assert.False(result.Complete);
        Assert.NotEmpty(result.PoliciesLeft);
        Assert.All(result.PoliciesLeft, value => Assert.StartsWith($@"HKU\{Noa}\", value, StringComparison.Ordinal));
        Assert.Contains($@"left: HKU\{Noa}\", _log.ToString(), StringComparison.Ordinal);
        Assert.Empty(_store.ValuesOf(Yoav));
        Assert.True(result.DataDeleted);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void Wfp_filters_are_removed_when_an_engine_exists()
    {
        var engine = new FakeWfp();
        engine.SetUserPolicy(new UserNetworkPolicy(Noa, false, ["chrome.exe"], []));

        var result = Cleanup(engine);

        Assert.True(result.WfpRemoved);
        Assert.True(engine.RemovedAll);
        Assert.Empty(engine.FilteredUsers());
    }

    [Fact]
    public void A_failing_wfp_engine_is_reported_and_does_not_stop_the_cleanup()
    {
        ServiceWrotePolicies();

        var result = Cleanup(new FakeWfp { Fails = true });

        Assert.False(result.WfpRemoved);
        Assert.False(result.Complete);
        Assert.Empty(_store.ValuesOf(Noa));
        Assert.True(result.DataDeleted);
    }

    [Fact]
    public void The_data_folder_is_deleted_with_everything_in_it()
    {
        ServiceWrotePolicies();

        var result = Cleanup();

        Assert.True(result.DataDeleted);
        Assert.False(Directory.Exists(_root));
        Assert.Contains("deleted", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Running_again_or_on_a_machine_that_never_applied_policies_is_harmless()
    {
        ServiceWrotePolicies();
        Assert.True(Cleanup().Complete);

        var again = Cleanup();

        Assert.True(again.Complete);
        Assert.Equal(0, again.PoliciesRemoved);
    }

    [Fact]
    public void An_unreadable_state_file_is_reported_and_the_data_folder_still_goes()
    {
        File.WriteAllText(StatePath, "not json");

        var result = Cleanup();

        Assert.Equal([StatePath], result.PoliciesLeft);
        Assert.True(result.DataDeleted);
    }

    [Fact]
    public void A_paired_agent_unregisters_from_the_hub_before_its_pairing_is_deleted()
    {
        ServiceWrotePolicies();
        _hub.Answer = UnregisterOutcome.Unregistered;

        var result = Cleanup();

        Assert.True(result.Complete);
        Assert.Equal(UnregisterOutcome.Unregistered, result.Hub);
        Assert.Equal(1, _hub.Calls);
        Assert.True(_hub.PairingWasThere);
        Assert.False(Directory.Exists(_root));
        Assert.Contains("Hub: this computer was unregistered", _log.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UnregisterOutcome.NotPaired, "not paired with a hub")]
    [InlineData(UnregisterOutcome.AlreadyRevoked, "had already revoked this computer")]
    public void A_hub_or_unpaired_agent_and_an_already_revoked_one_are_reported_as_such(UnregisterOutcome answer, string logged)
    {
        _hub.Answer = answer;

        var result = Cleanup();

        Assert.True(result.Complete);
        Assert.Equal(answer, result.Hub);
        Assert.Contains(logged, _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_hub_that_is_off_is_reported_and_the_uninstall_still_completes()
    {
        ServiceWrotePolicies();
        _hub.Fails = new HttpRequestException("No connection could be made because the target machine actively refused it.");

        var result = Cleanup();

        Assert.Equal(UnregisterOutcome.Failed, result.Hub);
        Assert.True(result.Complete); // nothing is left on this computer
        Assert.Empty(_store.ValuesOf(Noa));
        Assert.False(Directory.Exists(_root));
        Assert.Contains("could not be unregistered (No connection could be made", _log.ToString(), StringComparison.Ordinal);
        Assert.Contains("until a parent revokes it there", _log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_hub_that_does_not_answer_is_given_up_on_after_the_timeout()
    {
        _hub.Hangs = true; // and ignores the cancellation

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = Cleanup();

        Assert.True(watch.Elapsed < HubTimeout + TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.Equal(UnregisterOutcome.Failed, result.Hub);
        Assert.True(result.DataDeleted);
        Assert.Contains("(no answer within 0.3 seconds)", _log.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Loads an unloaded user's values under the name <c>RegistryUserHives</c> uses.</summary>
    private sealed class FakeHives(MemoryPolicyStore store) : IUserHives
    {
        public List<string> Loaded { get; } = [];
        public HashSet<string> Broken { get; } = [];

        public string? Load(string userSid)
        {
            Loaded.Add(userSid);
            if (Broken.Contains(userSid)) return null;
            var name = "WebFilter.Cleanup." + userSid;
            store.LoadAs(userSid, name);
            return name;
        }
    }

    /// <summary>Stands in for the hub link: answers as told, and notes whether the pairing key was still on disk when asked.</summary>
    private sealed class FakeHubLink(string keyFile) : IHubUnregistration
    {
        public UnregisterOutcome Answer { get; set; } = UnregisterOutcome.NotPaired;
        public Exception? Fails { get; set; }
        public bool Hangs { get; set; }
        public int Calls { get; private set; }
        public bool PairingWasThere { get; private set; }

        public async Task<UnregisterOutcome> UnregisterAsync(CancellationToken ct)
        {
            Calls++;
            PairingWasThere = File.Exists(keyFile);
            await Task.Yield();
            if (Hangs) await new TaskCompletionSource().Task;
            if (Fails is not null) throw Fails;
            return Answer;
        }
    }

    private sealed class FakeWfp : IWfpEngine
    {
        private readonly HashSet<string> _users = [];
        public bool Fails { get; init; }
        public bool RemovedAll { get; private set; }
        public IReadOnlyCollection<string> FilteredUsers() => _users;
        public IReadOnlyList<SkippedApp> SetUserPolicy(UserNetworkPolicy policy)
        {
            _users.Add(policy.UserSid);
            return [];
        }

        public void RemoveUser(string userSid) => _users.Remove(userSid);

        public void RemoveAll()
        {
            if (Fails) throw new InvalidOperationException("FwpmEngineOpen0 failed");
            _users.Clear();
            RemovedAll = true;
        }
    }
}

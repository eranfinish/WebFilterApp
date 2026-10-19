using WebFilter.Core.Model;
using WebFilter.Service.Mapping;
using WebFilter.Service.Policies;
using WebFilter.Contracts.Hub;

namespace WebFilter.Service.Tests;

/// <summary>
/// M4 browser policies (MVP-PLAN §5) against an in-memory registry: who gets which values, removal of values no longer
/// wanted, tamper repair, users who are not logged on, and uninstall. The real registry is exercised only on the lab VM.
/// </summary>
public sealed class PolicyTests : IDisposable
{
    private const string Chrome = @"SOFTWARE\Policies\Google\Chrome";
    private const string Edge = @"SOFTWARE\Policies\Microsoft\Edge";
    private const string Noa = "S-1-5-21-2000-1001", Yoav = "S-1-5-21-2000-1002", Dana = "S-1-5-21-2000-1003", Savta = "S-1-5-21-2000-1004", Visitor = "S-1-5-21-2000-1005";
    private static readonly ExtensionIds Ids = new("chromeidchromeidchromeidchromeid", "edgeidedgeidedgeidedgeidedgeided");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-policy-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryStore _store = new();
    private readonly PolicyManager _manager;
    private FamilyConfig _config = Fixture();

    public PolicyTests() => _manager = new PolicyManager(_store, Path.Combine(_dir, "managed.json"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private IReadOnlyList<PolicyValue> Desired() => BrowserPolicies.Desired(_config, "pc-kids", Ids, @"C:\Program Files\WebFilter\com.webfilter.host.json");

    [Fact]
    public void Machine_policies_force_install_the_extension_and_register_the_host()
    {
        _manager.Apply(Desired());
        Assert.Equal($"{Ids.ChromeId};{ExtensionIds.ChromeUpdateUrl}", _store.Machine($@"{Chrome}\ExtensionInstallForcelist", "1"));
        Assert.Equal($"{Ids.EdgeId};{ExtensionIds.EdgeUpdateUrl}", _store.Machine($@"{Edge}\ExtensionInstallForcelist", "1"));
        Assert.Equal(0, _store.Machine(Chrome, "NativeMessagingUserLevelHosts"));
        Assert.Equal(0, _store.Machine(Edge, "NativeMessagingUserLevelHosts"));
        Assert.Equal(@"C:\Program Files\WebFilter\com.webfilter.host.json", _store.Machine(@"SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.webfilter.host", ""));
    }

    [Fact]
    public void Without_store_ids_no_force_install_is_written()
    {
        var values = BrowserPolicies.Desired(_config, "pc-kids", new ExtensionIds(null, null), "m.json");
        Assert.DoesNotContain(values, v => v.KeyPath.EndsWith("ExtensionInstallForcelist", StringComparison.Ordinal));
    }

    [Fact]
    public void Restricted_and_unlinked_accounts_are_locked_down_adults_and_admins_are_not()
    {
        _manager.Apply(Desired());
        foreach (var sid in new[] { Noa, Yoav, Visitor })
        {
            Assert.Equal(1, _store.User(sid, Chrome, "IncognitoModeAvailability"));
            Assert.Equal(1, _store.User(sid, Edge, "InPrivateModeAvailability"));
            foreach (var browser in new[] { Chrome, Edge })
            {
                Assert.Equal(0, _store.User(sid, browser, "BrowserGuestModeEnabled"));
                Assert.Equal(0, _store.User(sid, browser, "BrowserAddPersonEnabled"));
                Assert.Equal(1, _store.User(sid, browser, "ExtensionDeveloperModeSettings"));
                Assert.Equal(2, _store.User(sid, browser, "DeveloperToolsAvailability"));
                Assert.Equal("*", _store.User(sid, $@"{browser}\ExtensionInstallBlocklist", "1"));
                Assert.Equal(1, _store.User(sid, browser, "ForceGoogleSafeSearch"));
            }
        }
        Assert.Empty(_store.ValuesOf(Dana));  // admin
        Assert.Empty(_store.ValuesOf(Savta)); // unrestricted adult
    }

    [Fact]
    public void SafeSearch_and_YouTube_strength_follow_the_profile()
    {
        _manager.Apply(Desired());
        Assert.Equal(2, _store.User(Noa, Chrome, "ForceYouTubeRestrict"));  // child: strict
        Assert.Equal(1, _store.User(Yoav, Chrome, "ForceYouTubeRestrict")); // teen: moderate
        Assert.Equal(1, _store.User(Visitor, Edge, "ForceYouTubeRestrict"));
        Assert.Equal(2, _store.User(Noa, Edge, "ForceBingSafeSearch"));
        Assert.Equal(1, _store.User(Yoav, Edge, "ForceBingSafeSearch"));
    }

    [Fact]
    public void Lifting_restrictions_removes_exactly_the_values_we_wrote()
    {
        _store.Set(PolicyHive.User, Yoav, Chrome, "SomeoneElsesPolicy", 7);
        _manager.Apply(Desired());
        _config = Fixture(m => m.Id == "yoav" ? m with { FilterMode = FilterMode.Unrestricted } : m);

        var result = _manager.Apply(Desired());

        Assert.True(result.Deleted > 0);
        Assert.Equal(new[] { "SomeoneElsesPolicy" }, _store.ValuesOf(Yoav).Select(v => v.Name));
        Assert.Equal(1, _store.User(Noa, Chrome, "IncognitoModeAvailability"));
    }

    [Fact]
    public void A_changed_value_is_restored_and_reported()
    {
        _manager.Apply(Desired());
        _store.Set(PolicyHive.User, Noa, Chrome, "IncognitoModeAvailability", 0);
        _store.Delete(PolicyHive.User, Noa, Edge, "DeveloperToolsAvailability");

        var result = _manager.Apply(Desired());

        Assert.Equal(2, result.Written);
        Assert.Single(result.Repaired, r => r.EndsWith(@"Chrome\IncognitoModeAvailability", StringComparison.Ordinal));
        Assert.Equal(1, _store.User(Noa, Chrome, "IncognitoModeAvailability"));
        Assert.Equal(2, _store.User(Noa, Edge, "DeveloperToolsAvailability"));
        Assert.Equal(0, _manager.Apply(Desired()).Written);
    }

    [Fact]
    public void A_user_who_is_not_logged_on_is_handled_once_their_hive_loads()
    {
        _store.Unloaded.Add(Noa);
        var first = _manager.Apply(Desired());
        Assert.True(first.Skipped > 0);
        Assert.Empty(_store.ValuesOf(Noa));

        _store.Unloaded.Remove(Noa);
        _manager.Apply(Desired());
        Assert.Equal(1, _store.User(Noa, Chrome, "IncognitoModeAvailability"));
    }

    [Fact]
    public void Managed_values_are_remembered_across_restarts_and_removed_on_uninstall()
    {
        _store.Set(PolicyHive.Machine, null, Chrome, "SomeoneElsesPolicy", 1);
        _manager.Apply(Desired());
        var restarted = new PolicyManager(_store, Path.Combine(_dir, "managed.json"));

        Assert.True(restarted.RemoveAll() > 0);

        Assert.Equal(new[] { "SomeoneElsesPolicy" }, _store.All.Select(v => v.Name));
    }

    private static FamilyConfig Fixture(Func<FamilyMember, FamilyMember>? patch = null)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "WebFilter.slnx"))) root = root.Parent!;
        var config = BundleMapper.ToConfig(FamilyBundleJson.Deserialize(File.ReadAllText(Path.Combine(root.FullName, "tests", "simulation", "fixtures", "family-base.json"))));
        if (patch is not null)
            for (var i = 0; i < config.Members.Count; i++) config.Members[i] = patch(config.Members[i]);
        return config;
    }

    private sealed class MemoryStore : IPolicyStore
    {
        private readonly Dictionary<(PolicyHive, string?, string, string), object> _values = [];
        public HashSet<string> Unloaded { get; } = [];

        public IEnumerable<(string? Sid, string Key, string Name)> All => _values.Keys.Select(k => (k.Item2, k.Item3, k.Item4));
        public IEnumerable<(string Key, string Name)> ValuesOf(string sid) => _values.Keys.Where(k => k.Item2 == sid).Select(k => (k.Item3, k.Item4));
        public object? Machine(string key, string name) => Get(PolicyHive.Machine, null, key, name);
        public object? User(string sid, string key, string name) => Get(PolicyHive.User, sid, key, name);

        public bool IsAvailable(PolicyHive hive, string? userSid) => hive == PolicyHive.Machine || !Unloaded.Contains(userSid!);
        public object? Get(PolicyHive hive, string? userSid, string keyPath, string name) => _values.GetValueOrDefault((hive, userSid, keyPath, name));
        public void Set(PolicyHive hive, string? userSid, string keyPath, string name, object value) => _values[(hive, userSid, keyPath, name)] = value;
        public void Delete(PolicyHive hive, string? userSid, string keyPath, string name) => _values.Remove((hive, userSid, keyPath, name));
    }
}

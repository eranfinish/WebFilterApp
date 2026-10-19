using WebFilter.Service.Policies;

namespace WebFilter.Service.Tests;

/// <summary>
/// An in-memory registry for policy tests. A user in <see cref="Unloaded"/> is not logged on: their values stay (as in
/// their NTUSER.DAT) but are unreachable until <see cref="LoadAs"/> makes them reachable under another name, the way
/// uninstall loads a hive under HKEY_USERS.
/// </summary>
internal sealed class MemoryPolicyStore : IPolicyStore
{
    private readonly Dictionary<(PolicyHive, string?, string, string), object> _values = [];
    private readonly Dictionary<string, string> _loadedAs = [];

    public HashSet<string> Unloaded { get; } = [];

    public IEnumerable<(string? Sid, string Key, string Name)> All => _values.Keys.Select(k => (k.Item2, k.Item3, k.Item4));
    public IEnumerable<(string Key, string Name)> ValuesOf(string sid) => _values.Keys.Where(k => k.Item2 == sid).Select(k => (k.Item3, k.Item4));
    public object? Machine(string key, string name) => Get(PolicyHive.Machine, null, key, name);
    public object? User(string sid, string key, string name) => Get(PolicyHive.User, sid, key, name);

    /// <summary>Makes an unloaded user's values reachable as HKEY_USERS\<paramref name="name"/>.</summary>
    public void LoadAs(string sid, string name) => _loadedAs[name] = sid;

    private string? Owner(PolicyHive hive, string? name) => hive == PolicyHive.User && name is not null && _loadedAs.TryGetValue(name, out var sid) ? sid : name;

    public bool IsAvailable(PolicyHive hive, string? userSid) =>
        hive == PolicyHive.Machine || _loadedAs.ContainsKey(userSid!) || !Unloaded.Contains(userSid!);

    public object? Get(PolicyHive hive, string? userSid, string keyPath, string name)
    {
        Reachable(hive, userSid);
        return _values.GetValueOrDefault((hive, Owner(hive, userSid), keyPath, name));
    }

    public void Set(PolicyHive hive, string? userSid, string keyPath, string name, object value)
    {
        Reachable(hive, userSid);
        _values[(hive, Owner(hive, userSid), keyPath, name)] = value;
    }

    public void Delete(PolicyHive hive, string? userSid, string keyPath, string name)
    {
        Reachable(hive, userSid);
        _values.Remove((hive, Owner(hive, userSid), keyPath, name));
    }

    /// <summary>Writing to HKEY_USERS\&lt;SID&gt; of a user who is not logged on fails in the real registry too.</summary>
    private void Reachable(PolicyHive hive, string? userSid)
    {
        if (!IsAvailable(hive, userSid)) throw new UnauthorizedAccessException($@"HKEY_USERS\{userSid} is not loaded");
    }
}

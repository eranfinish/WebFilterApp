using WebFilter.Core.Engine;
using WebFilter.Service.Network;

namespace WebFilter.Service.Tests;

public sealed class WfpManagerTests
{
    private readonly FakeEngine _engine = new();
    private static UserNetworkPolicy Policy(string sid, bool timeUp = false, params string[] paths) => new(sid, timeUp, paths, []);

    [Fact]
    public void Only_changes_reach_wfp()
    {
        var manager = new WfpManager(_engine);
        Assert.Equal(2, manager.Apply([Policy("A", false, "x.exe"), Policy("B")]).Set);
        Assert.Equal(0, manager.Apply([Policy("A", false, "x.exe"), Policy("B")]).Set);

        var result = manager.Apply([Policy("A", true, "x.exe"), Policy("B")]);
        Assert.Equal(1, result.Set);
        Assert.True(_engine.Users["A"].TimeUp);
    }

    [Fact]
    public void Accounts_that_no_longer_need_filters_are_cleared_including_ones_left_from_before_a_restart()
    {
        _engine.Users["OLD"] = Policy("OLD");
        var manager = new WfpManager(_engine);
        var result = manager.Apply([Policy("A")]);
        Assert.Equal((1, 1), (result.Set, result.Removed));
        Assert.Equal(["A"], _engine.Users.Keys);

        Assert.Equal(1, manager.Apply([]).Removed);
        Assert.Empty(_engine.Users);
    }

    [Fact]
    public void Deleted_filters_are_put_back_and_reported()
    {
        var manager = new WfpManager(_engine);
        manager.Apply([Policy("A", false, "x.exe")]);
        _engine.Users.Remove("A");

        var result = manager.Apply([Policy("A", false, "x.exe")]);

        Assert.Equal(["A"], result.Repaired);
        Assert.True(_engine.Users.ContainsKey("A"));
    }

    private sealed class FakeEngine : IWfpEngine
    {
        public Dictionary<string, UserNetworkPolicy> Users { get; } = [];
        public IReadOnlyCollection<string> FilteredUsers() => Users.Keys.ToList();
        public void SetUserPolicy(UserNetworkPolicy policy) => Users[policy.UserSid] = policy;
        public void RemoveUser(string userSid) => Users.Remove(userSid);
        public void RemoveAll() => Users.Clear();
    }
}

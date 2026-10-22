using WebFilter.Core.Engine;
using WebFilter.Service.Network;

namespace WebFilter.Service.Tests;

public sealed class WfpManagerTests
{
    private const string Game = @"C:\Games\game.exe";
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

    [Fact]
    public void A_program_that_is_not_installed_is_reported_once_and_gets_its_filter_when_it_appears()
    {
        _engine.Errors[Game] = 2; // ERROR_FILE_NOT_FOUND
        var installed = false;
        var manager = new WfpManager(_engine, path => path != Game || installed);

        var first = manager.Apply([Policy("A", false, "chrome.exe", Game)]);
        Assert.Equal([("A", new SkippedApp(Game, 2))], first.Skipped);
        Assert.True(first.Skipped.Single().App.NotInstalled);

        // Still missing: nothing to do, and nothing new to report.
        var again = manager.Apply([Policy("A", false, "chrome.exe", Game)]);
        Assert.Equal((0, 0), (again.Set, again.Skipped.Count));

        // Installed now: the account is set again, and this time the program gets its filter.
        installed = true;
        _engine.Errors.Remove(Game);
        var afterInstall = manager.Apply([Policy("A", false, "chrome.exe", Game)]);
        Assert.Equal((1, 0), (afterInstall.Set, afterInstall.Skipped.Count));
        Assert.Equal(0, manager.Apply([Policy("A", false, "chrome.exe", Game)]).Set);
    }

    [Fact]
    public void A_program_skipped_for_another_reason_is_not_set_again_on_every_pass()
    {
        _engine.Errors[Game] = 5; // ERROR_ACCESS_DENIED: the file is there, retrying would not help
        var manager = new WfpManager(_engine, _ => true);

        Assert.False(manager.Apply([Policy("A", false, Game)]).Skipped.Single().App.NotInstalled);
        Assert.Equal(0, manager.Apply([Policy("A", false, Game)]).Set);
        Assert.Equal(1, _engine.Sets);
    }

    [Fact]
    public void A_skipped_program_is_reported_again_after_the_account_was_cleared()
    {
        _engine.Errors[Game] = 2;
        var manager = new WfpManager(_engine, _ => false);
        manager.Apply([Policy("A", false, Game)]);
        manager.Apply([]);

        Assert.Single(manager.Apply([Policy("A", false, Game)]).Skipped);
    }

    [Fact]
    public void An_account_wfp_refuses_does_not_stop_the_others_and_is_tried_again()
    {
        _engine.Refused.Add("BAD");
        var manager = new WfpManager(_engine);

        var result = manager.Apply([Policy("A"), Policy("BAD"), Policy("B")]);

        Assert.Equal(["BAD"], result.Failed.Select(f => f.UserSid));
        Assert.Equal(2, result.Set);
        Assert.Equal(["A", "B"], _engine.Users.Keys.Order());

        _engine.Refused.Clear();
        result = manager.Apply([Policy("A"), Policy("BAD"), Policy("B")]);
        Assert.Equal((1, 0), (result.Set, result.Failed.Count));
        Assert.Contains("BAD", _engine.Users.Keys);
    }

    private sealed class FakeEngine : IWfpEngine
    {
        public Dictionary<string, UserNetworkPolicy> Users { get; } = [];

        /// <summary>Accounts WFP refuses, as it refuses an id that is not a SID (ERROR_INVALID_SID).</summary>
        public HashSet<string> Refused { get; } = [];

        /// <summary>The Win32 error a program path gets instead of a filter.</summary>
        public Dictionary<string, int> Errors { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int Sets { get; private set; }

        public IReadOnlyCollection<string> FilteredUsers() => Users.Keys.ToList();

        public IReadOnlyList<SkippedApp> SetUserPolicy(UserNetworkPolicy policy)
        {
            if (Refused.Contains(policy.UserSid)) throw new System.ComponentModel.Win32Exception(1337, "WFP: could not set the network filters");
            Users[policy.UserSid] = policy;
            Sets++;
            return policy.AllowedPaths.Where(Errors.ContainsKey).Select(p => new SkippedApp(p, Errors[p])).ToList();
        }

        public void RemoveUser(string userSid) => Users.Remove(userSid);
        public void RemoveAll() => Users.Clear();
    }
}

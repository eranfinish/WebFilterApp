using System.Text.Json.Nodes;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Core.Tests.Simulation;

namespace WebFilter.Core.Tests.Engine;

/// <summary>
/// PROTECTION-SPEC §14: a browser that runs in a restricted account without a live connection from the extension keeps its
/// network for the download window, is blocked after it until the extension connects, and gets a new window when it starts
/// again. pc-kids in the fixture: noa and yoav are restricted, visitor is unlinked, dana an administrator, savta unrestricted.
/// </summary>
public class ExtensionGuardTests
{
    private const string Noa = "S-1-5-21-2000-1001", Yoav = "S-1-5-21-2000-1002", Dana = "S-1-5-21-2000-1003", Savta = "S-1-5-21-2000-1004",
        Visitor = "S-1-5-21-2000-1005";
    private const string ChromeId = "cccccccccccccccccccccccccccccccc", EdgeId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-02T15:30:00+02:00", System.Globalization.CultureInfo.InvariantCulture); // Monday, in the window

    private static readonly TimeSpan Grace = ExtensionGuardOptions.DefaultGrace, Stale = ExtensionGuardOptions.DefaultStaleness;

    private static BrowserProcess Chrome(string account, int pid = 100) => new(account, BrowserKind.Chrome, pid, T0.AddHours(-1).AddSeconds(pid));
    private static BrowserProcess Edge(string account, int pid = 200) => new(account, BrowserKind.Edge, pid, T0.AddHours(-1).AddSeconds(pid));

    private static ExtensionConnection Connection(string account, BrowserKind browser, DateTimeOffset lastHeard, string? id = null) =>
        new(account, browser, id ?? (browser == BrowserKind.Edge ? EdgeId : ChromeId), lastHeard);

    /// <summary>
    /// A guard as the service runs it: a pass every 10 seconds. <see cref="At"/> runs the passes since the last one up to
    /// <c>now</c> with the same processes and connections and returns what they did (the blocks as of the last one);
    /// <see cref="Gap"/> runs one pass after a long pause (the computer slept). Guards noa, yoav and visitor (the accounts the
    /// fixture filters at <see cref="T0"/>) with the store ids known, or the accounts of <paramref name="hub"/>'s settings.
    /// </summary>
    private sealed class Run(ExtensionGuardOptions? options = null, HubCore? hub = null)
    {
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
        private DateTimeOffset? _last;

        public ExtensionGuard Guard { get; } = new(options ?? new ExtensionGuardOptions(Grace, Stale, ChromeId, EdgeId));

        public ExtensionGuardResult At(DateTimeOffset now, IEnumerable<BrowserProcess> running, params ExtensionConnection[] connections)
        {
            var processes = running.ToList();
            List<AccountBrowser> started = [], restored = [], ended = [];
            for (var t = _last + Interval; t < now; t += Interval) Collect(Pass(t.Value, processes, connections));
            var result = Pass(now, processes, connections);
            Collect(result);
            return result with { Started = started, Restored = restored, Ended = ended };

            void Collect(ExtensionGuardResult r)
            {
                started.AddRange(r.Started);
                restored.AddRange(r.Restored);
                ended.AddRange(r.Ended);
            }
        }

        public ExtensionGuardResult Gap(DateTimeOffset now, IEnumerable<BrowserProcess> running, params ExtensionConnection[] connections) =>
            Pass(now, running.ToList(), connections);

        private ExtensionGuardResult Pass(DateTimeOffset now, List<BrowserProcess> running, ExtensionConnection[] connections)
        {
            _last = now;
            return hub is null ? Guard.Evaluate([Noa, Yoav, Visitor], running, connections, now) : Guard.Evaluate(hub.View(), "pc-kids", running, connections, now);
        }
    }

    [Fact]
    public void A_browser_without_the_extension_keeps_its_network_for_the_download_window_then_is_blocked_until_the_extension_connects()
    {
        var run = new Run();

        var start = run.At(T0, [Chrome(Noa)]);
        Assert.Empty(start.Blocked);
        Assert.Empty(start.Started); // no alarm for the window itself

        Assert.Empty(run.At(T0 + Grace - TimeSpan.FromSeconds(1), [Chrome(Noa)]).Blocked);

        var after = run.At(T0 + Grace, [Chrome(Noa)]);
        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Chrome)], after.Blocked);
        Assert.Equal(after.Blocked, after.Started);

        var still = run.At(T0 + Grace + TimeSpan.FromSeconds(10), [Chrome(Noa)]);
        Assert.Equal(after.Blocked, still.Blocked);
        Assert.Empty(still.Started); // reported once, when it starts

        // The extension connects (installed, or approved in the store): the block is lifted at once.
        var connected = T0 + Grace + TimeSpan.FromSeconds(20);
        var restored = run.At(connected, [Chrome(Noa)], Connection(Noa, BrowserKind.Chrome, connected));
        Assert.Empty(restored.Blocked);
        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Chrome)], restored.Restored);
    }

    [Fact]
    public void An_extension_that_connects_within_the_window_is_never_blocked()
    {
        var run = new Run();
        run.At(T0, [Edge(Yoav)]);
        for (var t = TimeSpan.FromSeconds(10); t < TimeSpan.FromMinutes(20); t += TimeSpan.FromSeconds(10))
        {
            // Heartbeats every 30 seconds.
            var heard = T0 + TimeSpan.FromSeconds(Math.Floor(t.TotalSeconds / 30) * 30);
            var result = run.At(T0 + t, [Edge(Yoav)], Connection(Yoav, BrowserKind.Edge, heard));
            Assert.Empty(result.Blocked);
            Assert.Empty(result.Started);
        }
    }

    [Fact]
    public void Starting_the_browser_again_opens_a_new_download_window()
    {
        var run = new Run();
        run.At(T0, [Chrome(Noa, 100)]);
        Assert.NotEmpty(run.At(T0 + Grace, [Chrome(Noa, 100)]).Blocked);

        // Closed: nothing runs, nothing is blocked.
        var closed = run.At(T0 + Grace + TimeSpan.FromSeconds(10), []);
        Assert.Empty(closed.Blocked);
        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Chrome)], closed.Ended);

        // Opened again: a new window from its start.
        var again = T0 + Grace + TimeSpan.FromSeconds(20);
        Assert.Empty(run.At(again, [Chrome(Noa, 300)]).Blocked);
        Assert.Empty(run.At(again + Grace - TimeSpan.FromSeconds(1), [Chrome(Noa, 300)]).Blocked);
        Assert.NotEmpty(run.At(again + Grace, [Chrome(Noa, 300)]).Started);
    }

    [Fact]
    public void A_restart_between_two_passes_is_a_new_start_too_but_a_browser_that_keeps_running_is_not()
    {
        var run = new Run();
        run.At(T0, [Chrome(Noa, 100), Chrome(Noa, 101)]);
        Assert.NotEmpty(run.At(T0 + Grace, [Chrome(Noa, 100), Chrome(Noa, 101)]).Blocked);

        // Tabs come and go (renderers), the browser process stays: the same run, still blocked.
        Assert.NotEmpty(run.At(T0 + Grace + TimeSpan.FromSeconds(10), [Chrome(Noa, 100), Chrome(Noa, 102)]).Blocked);

        // Closed and opened again within one poll: none of the processes seen before is left.
        var restarted = run.At(T0 + Grace + TimeSpan.FromSeconds(20), [Chrome(Noa, 400), Chrome(Noa, 401)]);
        Assert.Empty(restarted.Blocked);
        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Chrome)], restarted.Ended);

        // A process id Windows reused for a new process is a new process (its start time differs).
        var reused = new BrowserProcess(Noa, BrowserKind.Chrome, 400, T0.AddMinutes(30));
        var start = T0 + Grace + TimeSpan.FromSeconds(30);
        run.At(start + Grace, [Chrome(Noa, 400)]);
        Assert.Empty(run.At(start + Grace + TimeSpan.FromSeconds(10), [reused]).Blocked);
    }

    [Fact]
    public void Nothing_is_blocked_while_the_browser_does_not_run()
    {
        var run = new Run();
        for (var t = TimeSpan.Zero; t < TimeSpan.FromMinutes(10); t += TimeSpan.FromSeconds(10))
        {
            var result = run.At(T0 + t, [new BrowserProcess(Noa, BrowserKind.Edge, 1, T0)]);
            Assert.DoesNotContain(result.Blocked, b => b.Browser == BrowserKind.Chrome);
        }
        Assert.Empty(new Run().At(T0 + TimeSpan.FromHours(1), []).Blocked);
    }

    [Fact]
    public void Each_browser_is_guarded_on_its_own()
    {
        var run = new Run();
        run.At(T0, [Chrome(Noa), Edge(Noa)]);
        var now = T0 + Grace;

        // Edge's extension is connected (0.3.0), Chrome's is not (the 0.1.0 stub never connects).
        var result = run.At(now, [Chrome(Noa), Edge(Noa)], Connection(Noa, BrowserKind.Edge, now - TimeSpan.FromSeconds(5)));

        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Chrome)], result.Blocked);
        // Chrome's session does not cover Edge, nor noa's yoav's.
        var swapped = new Run();
        swapped.At(T0, [Edge(Noa), Chrome(Yoav)]);
        var other = swapped.At(now, [Edge(Noa), Chrome(Yoav)], Connection(Noa, BrowserKind.Chrome, now), Connection(Noa, BrowserKind.Edge, now, ChromeId));
        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Edge), new AccountBrowser(Yoav, BrowserKind.Chrome)],
            other.Blocked.OrderBy(b => b.AccountId, StringComparer.Ordinal));
    }

    [Fact]
    public void Administrators_unrestricted_and_paused_members_are_never_blocked_unlinked_accounts_are()
    {
        var hub = new HubCore(Fixture());
        hub.Pause("yoav", T0.AddHours(2), T0);
        var run = new Run(hub: hub);
        BrowserProcess[] everyone = [Chrome(Noa), Chrome(Yoav), Chrome(Dana), Chrome(Savta), Chrome(Visitor)];

        Assert.Equal(new[] { Noa, Visitor }.Order(), ExtensionGuard.GuardedAccounts(hub.View(), "pc-kids", T0).Order());
        run.At(T0, everyone);
        var result = run.At(T0 + Grace, everyone);

        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Chrome), new AccountBrowser(Visitor, BrowserKind.Chrome)], result.Blocked);

        // The pause ends: yoav is guarded from then, with a download window of his own.
        var later = T0.AddHours(2);
        Assert.DoesNotContain(new AccountBrowser(Yoav, BrowserKind.Chrome), run.At(later + Grace - TimeSpan.FromSeconds(1), everyone).Blocked);
        Assert.Contains(new AccountBrowser(Yoav, BrowserKind.Chrome), run.At(later + Grace, everyone).Blocked);
        Assert.DoesNotContain(run.At(later + Grace + TimeSpan.FromHours(1), everyone).Blocked, b => b.AccountId == Dana || b.AccountId == Savta);
    }

    [Fact]
    public void A_member_paused_while_blocked_gets_the_browser_back()
    {
        var hub = new HubCore(Fixture());
        var run = new Run(hub: hub);
        run.At(T0, [Chrome(Yoav)]);
        Assert.NotEmpty(run.At(T0 + Grace, [Chrome(Yoav)]).Blocked);

        hub.Pause("yoav", T0.AddHours(1), T0 + Grace);
        var paused = run.At(T0 + Grace + TimeSpan.FromSeconds(10), [Chrome(Yoav)]);

        Assert.Empty(paused.Blocked);
        Assert.Equal([new AccountBrowser(Yoav, BrowserKind.Chrome)], paused.Ended);
    }

    [Fact]
    public void A_stale_connection_counts_as_missing()
    {
        var run = new Run();
        var now = T0 + Grace;
        run.At(T0, [Chrome(Noa), Edge(Noa)]);

        // Edge was heard from 90 seconds ago (three missed heartbeats): still live. Chrome 91 seconds ago: missing.
        var result = run.At(now, [Chrome(Noa), Edge(Noa)], Connection(Noa, BrowserKind.Chrome, now - Stale - TimeSpan.FromSeconds(1)),
            Connection(Noa, BrowserKind.Edge, now - Stale));

        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Chrome)], result.Blocked);
    }

    [Fact]
    public void A_connection_lost_after_the_window_blocks_once_it_is_stale()
    {
        var run = new Run();
        run.At(T0, [Chrome(Noa)], Connection(Noa, BrowserKind.Chrome, T0));
        var lastHeard = T0 + TimeSpan.FromMinutes(10);
        Assert.Empty(run.At(lastHeard, [Chrome(Noa)], Connection(Noa, BrowserKind.Chrome, lastHeard)).Blocked);

        // The extension stops (disabled by an administrator): its last message keeps counting for the staleness limit,
        // which also covers an extension that reloads and reconnects.
        Assert.Empty(run.At(lastHeard + Stale, [Chrome(Noa)], Connection(Noa, BrowserKind.Chrome, lastHeard)).Blocked);
        var blocked = run.At(lastHeard + Stale + TimeSpan.FromSeconds(10), [Chrome(Noa)], Connection(Noa, BrowserKind.Chrome, lastHeard));
        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Chrome)], blocked.Started);
    }

    [Fact]
    public void Only_a_session_from_the_store_extension_counts_when_its_id_is_known()
    {
        var run = new Run();
        var now = T0 + Grace;
        run.At(T0, [Chrome(Noa), Edge(Noa), Chrome(Yoav)]);

        var result = run.At(now, [Chrome(Noa), Edge(Noa), Chrome(Yoav)],
            Connection(Noa, BrowserKind.Chrome, now, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), // another extension: not ours
            Connection(Noa, BrowserKind.Edge, now, EdgeId.ToUpperInvariant()),           // ours, whatever the case
            new ExtensionConnection(Yoav, BrowserKind.Chrome, null, now));                // a hello without an id

        Assert.Equal([new AccountBrowser(Noa, BrowserKind.Chrome), new AccountBrowser(Yoav, BrowserKind.Chrome)],
            result.Blocked.OrderBy(b => b.AccountId, StringComparer.Ordinal));

        // Without known store ids (a development run), any id counts.
        var open = new Run(new ExtensionGuardOptions(Grace, Stale));
        open.At(T0, [Chrome(Noa)]);
        Assert.Empty(open.At(now, [Chrome(Noa)], Connection(Noa, BrowserKind.Chrome, now, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).Blocked);
    }

    [Fact]
    public void After_a_gap_such_as_sleep_no_block_starts_until_the_extensions_could_be_heard_from_again()
    {
        var run = new Run();
        run.At(T0, [Chrome(Noa)], Connection(Noa, BrowserKind.Chrome, T0));
        // The computer sleeps for an hour; on waking the guard runs before the extension's next heartbeat.
        var wake = T0 + TimeSpan.FromHours(1);
        Assert.Empty(run.Gap(wake, [Chrome(Noa)], Connection(Noa, BrowserKind.Chrome, T0)).Started);
        Assert.Empty(run.At(wake + TimeSpan.FromSeconds(10), [Chrome(Noa)], Connection(Noa, BrowserKind.Chrome, wake + TimeSpan.FromSeconds(5))).Blocked);

        // An extension that stays silent is blocked once that wait is over.
        var silent = new Run();
        silent.At(T0, [Chrome(Noa)]);
        Assert.Empty(silent.Gap(wake, [Chrome(Noa)]).Blocked);
        Assert.Empty(silent.At(wake + Stale - TimeSpan.FromSeconds(1), [Chrome(Noa)]).Blocked);
        Assert.NotEmpty(silent.At(wake + Stale, [Chrome(Noa)]).Started);
    }

    [Fact]
    public void Accounts_are_matched_without_case_and_processes_of_other_accounts_are_ignored()
    {
        var run = new Run();
        run.At(T0, [Chrome(Noa.ToLowerInvariant()), Chrome("S-1-5-21-9-9")]);
        var now = T0 + Grace;

        var result = run.At(now, [Chrome(Noa.ToLowerInvariant()), Chrome("S-1-5-21-9-9")], Connection(Noa, BrowserKind.Chrome, now));

        Assert.Empty(result.Blocked);
    }

    [Fact]
    public void The_window_follows_the_option()
    {
        var run = new Run(new ExtensionGuardOptions(TimeSpan.FromMinutes(10), Stale));
        run.At(T0, [Chrome(Noa)]);
        Assert.Empty(run.At(T0 + TimeSpan.FromMinutes(9), [Chrome(Noa)]).Blocked);
        Assert.NotEmpty(run.At(T0 + TimeSpan.FromMinutes(10), [Chrome(Noa)]).Blocked);
    }

    [Fact]
    public void A_browsers_paths_are_its_machine_wide_copies_and_the_familys_supported_ones_by_file_name()
    {
        var config = Fixture();
        config.SupportedBrowsers.Add(@"D:\Browsers\Chrome\chrome.exe");
        var programs = WindowsPrograms.Default with
        {
            MachineBrowsers = [@"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"],
        };

        Assert.Equal([@"C:\Program Files\Google\Chrome\Application\chrome.exe", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"D:\Browsers\Chrome\chrome.exe"], BrowserKind.Chrome.Paths(config, programs));
        Assert.Equal([@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"], BrowserKind.Edge.Paths(config, programs));
        Assert.True(BrowserKinds.TryParse("Edge", out var edge) && edge == BrowserKind.Edge);
        Assert.False(BrowserKinds.TryParse("firefox", out _));
    }

    internal static FamilyConfig Fixture() =>
        FixtureLoader.Load(JsonNode.Parse(File.ReadAllText(Path.Combine(EngineScenarioTests.SimulationDir, "fixtures", "family-base.json")))!.AsObject(), null);
}

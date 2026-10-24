using WebFilter.Core.Model;

namespace WebFilter.Core.Engine;

/// <summary>The browsers the extension filters in (MVP-PLAN §2.1). Wire names "chrome" and "edge", as the extension's hello says.</summary>
public enum BrowserKind { Chrome, Edge }

public static class BrowserKinds
{
    public static readonly IReadOnlyList<BrowserKind> All = [BrowserKind.Chrome, BrowserKind.Edge];

    public static string ToWire(this BrowserKind browser) => browser == BrowserKind.Edge ? "edge" : "chrome";

    /// <summary>"chrome" or "edge" (any case); anything else is not a browser the guard knows.</summary>
    public static bool TryParse(string? wire, out BrowserKind browser)
    {
        browser = BrowserKind.Chrome;
        if (string.Equals(wire, "chrome", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(wire, "edge", StringComparison.OrdinalIgnoreCase)) return false;
        browser = BrowserKind.Edge;
        return true;
    }

    /// <summary>The browser's program file: chrome.exe or msedge.exe.</summary>
    public static string FileName(this BrowserKind browser) => browser == BrowserKind.Edge ? "msedge.exe" : "chrome.exe";

    /// <summary>
    /// Where <paramref name="browser"/> may run with network for restricted accounts: the machine-wide copies the service
    /// found (<see cref="WindowsPrograms.MachineBrowsers"/>) and the family's <see cref="FamilyConfig.SupportedBrowsers"/>,
    /// those whose file is the browser's (<see cref="FileName"/>). The extension guard watches processes at these paths and,
    /// while it blocks the browser for an account, <see cref="NetworkPolicy.Desired"/> leaves them out of its allowlist.
    /// </summary>
    public static IReadOnlyList<string> Paths(this BrowserKind browser, FamilyConfig config, WindowsPrograms programs) =>
        programs.MachineBrowsers.Concat(config.SupportedBrowsers)
            .Where(p => string.Equals(FileOf(p), browser.FileName(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The file name of a Windows path ('\' or '/'), the same on every platform the Core runs on.</summary>
    private static string FileOf(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
}

/// <summary>A browser in one Windows account: what the guard watches, and blocks (<see cref="NetworkPolicy.Desired"/>).</summary>
public sealed record AccountBrowser(string AccountId, BrowserKind Browser);

/// <summary>
/// One running process of a guarded browser: the account it runs as (its token's user, a SID on Windows) and its identity —
/// the process id with its start time, so an id Windows reuses is a new process.
/// </summary>
public sealed record BrowserProcess(string AccountId, BrowserKind Browser, int ProcessId, DateTimeOffset StartedAt);

/// <summary>
/// One session of the extension with the service (through the native host): the account (from the pipe client's token),
/// the browser and extension id its hello named, and when its last message arrived. A session that has closed keeps the
/// time of its last message.
/// </summary>
public sealed record ExtensionConnection(string AccountId, BrowserKind Browser, string? ExtensionId, DateTimeOffset LastHeard);

/// <param name="Grace">
/// The download window: how long a browser that starts in a guarded account keeps its network without a live connection
/// from the extension, so it can install or update the extension from its store.
/// </param>
/// <param name="Staleness">
/// How recent the extension's last message must be for its connection to count as live. The extension sends a heartbeat
/// every 30 seconds (its alarm), so 90 seconds is three missed heartbeats: a slow or busy browser is not cut off, and an
/// extension that reloads (an update, a restarted service worker or native host) reconnects well within it.
/// </param>
/// <param name="ChromeExtensionId">The extension's Chrome Web Store id; a Chrome session with another id does not count. Null: not checked.</param>
/// <param name="EdgeExtensionId">The extension's Edge Add-ons id, likewise.</param>
public sealed record ExtensionGuardOptions(TimeSpan Grace, TimeSpan Staleness, string? ChromeExtensionId = null, string? EdgeExtensionId = null)
{
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan DefaultStaleness = TimeSpan.FromSeconds(90);

    public static ExtensionGuardOptions Default { get; } = new(DefaultGrace, DefaultStaleness);

    public string? ExtensionIdFor(BrowserKind browser) => browser == BrowserKind.Edge ? EdgeExtensionId : ChromeExtensionId;
}

/// <param name="Blocked">Every browser the guard blocks now, by account (sorted): what WFP leaves out.</param>
/// <param name="Started">Blocks that began in this pass: the parents are told (a tamper event).</param>
/// <param name="Restored">Blocks lifted in this pass because the extension connected.</param>
/// <param name="Ended">
/// Blocks dropped in this pass because the browser stopped (or started again: a new download window) or the account is no
/// longer guarded (paused, linked to an adult).
/// </param>
public sealed record ExtensionGuardResult(
    IReadOnlyList<AccountBrowser> Blocked,
    IReadOnlyList<AccountBrowser> Started,
    IReadOnlyList<AccountBrowser> Restored,
    IReadOnlyList<AccountBrowser> Ended);

/// <summary>
/// The extension guard (PROTECTION-SPEC §14): WFP lets the machine-wide Chrome and Edge reach the network for every
/// restricted account, and the filtering itself is the extension's; a browser whose extension is not working (not installed
/// yet, an old version, disabled by an administrator) would browse unfiltered. For every account
/// <see cref="NetworkPolicy.Desired"/> filters, and each browser:
/// <list type="bullet">
/// <item>a browser that starts running there without a live connection from its extension keeps its network for
/// <see cref="ExtensionGuardOptions.Grace"/> (the download window); a new start — all its processes had exited — opens a new one;</item>
/// <item>after that, still running and still without a live connection, it is blocked (taken out of the account's
/// allowlist) and the block is reported once, when it starts;</item>
/// <item>a live connection lifts the block at once; a browser that stops running is forgotten.</item>
/// </list>
/// Pure and single-threaded: the service feeds it processes and connections and passes the clock in. Keeps the state of
/// the browsers it has seen between calls.
/// </summary>
public sealed class ExtensionGuard(ExtensionGuardOptions? options = null)
{
    private readonly Dictionary<AccountBrowser, Watch> _watches = [];
    private DateTimeOffset? _lastPass;
    private DateTimeOffset _holdUntil = DateTimeOffset.MinValue;

    public ExtensionGuardOptions Options { get; } = options ?? ExtensionGuardOptions.Default;

    /// <summary>
    /// The accounts the guard watches on <paramref name="deviceId"/>: exactly those <see cref="NetworkPolicy.Desired"/> gives a
    /// policy (linked restricted members who are not paused, and unlinked standard accounts) — never administrators or
    /// unrestricted members, whose baseline protection (PROTECTION-SPEC §13) is not enforced.
    /// </summary>
    public static IReadOnlyList<string> GuardedAccounts(PolicyView view, string deviceId, DateTimeOffset now) =>
        NetworkPolicy.FilteredAccounts(view, deviceId, now).Select(a => a.AccountId).ToList();

    /// <summary>One pass over the family's settings: <see cref="Evaluate(IEnumerable{string}, IEnumerable{BrowserProcess}, IEnumerable{ExtensionConnection}, DateTimeOffset)"/> for the <see cref="GuardedAccounts"/>.</summary>
    public ExtensionGuardResult Evaluate(PolicyView view, string deviceId, IEnumerable<BrowserProcess> running, IEnumerable<ExtensionConnection> connections,
        DateTimeOffset now) =>
        Evaluate(GuardedAccounts(view, deviceId, now), running, connections, now);

    /// <summary>
    /// One pass: what runs now, and what the extension said and when. Processes of accounts not in
    /// <paramref name="guardedAccounts"/> are ignored.
    /// </summary>
    public ExtensionGuardResult Evaluate(IEnumerable<string> guardedAccounts, IEnumerable<BrowserProcess> running, IEnumerable<ExtensionConnection> connections,
        DateTimeOffset now)
    {
        // After a gap — the computer slept, the service was busy, the clock moved — the extensions have not had a chance to
        // be heard from again: no block starts until they have.
        if (_lastPass is not { } last || now - last > Options.Staleness || now < last) _holdUntil = now + Options.Staleness;
        _lastPass = now;

        var guarded = guardedAccounts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var runningNow = running
            .Where(p => guarded.Contains(p.AccountId))
            .GroupBy(p => Key(p.AccountId, p.Browser))
            .ToDictionary(g => g.Key, g => g.Select(p => (p.ProcessId, p.StartedAt)).ToHashSet());
        var live = connections.Where(c => IsLive(c, now)).Select(c => Key(c.AccountId, c.Browser)).ToHashSet();
        List<AccountBrowser> started = [], restored = [], ended = [];

        foreach (var key in _watches.Keys.Where(k => !runningNow.ContainsKey(k)).ToList())
        {
            if (_watches[key].Blocked) ended.Add(key);
            _watches.Remove(key);
        }
        foreach (var (key, processes) in runningNow)
        {
            if (_watches.TryGetValue(key, out var watch) && !processes.Overlaps(watch.Processes))
            {
                // Every process seen last time has exited and the browser runs again: a new start, with a new download window.
                if (watch.Blocked) ended.Add(key);
                watch = null;
            }
            watch ??= _watches[key] = new Watch(now);
            watch.Processes = processes;
            if (watch.RunningSince > now) watch.RunningSince = now; // the clock went back

            if (live.Contains(key))
            {
                if (watch.Blocked) restored.Add(key);
                watch.Blocked = false;
            }
            else if (!watch.Blocked && now - watch.RunningSince >= Options.Grace && now >= _holdUntil)
            {
                watch.Blocked = true;
                started.Add(key);
            }
        }

        var blocked = _watches.Where(w => w.Value.Blocked).Select(w => w.Key)
            .OrderBy(k => k.AccountId, StringComparer.Ordinal).ThenBy(k => k.Browser).ToList();
        return new ExtensionGuardResult(blocked, started, restored, ended);
    }

    /// <summary>
    /// Heard from within <see cref="ExtensionGuardOptions.Staleness"/> (a time ahead of the clock counts: the clock went
    /// back), and from the extension's store id when it is known — a session of another extension does not count.
    /// </summary>
    private bool IsLive(ExtensionConnection connection, DateTimeOffset now) =>
        now - connection.LastHeard <= Options.Staleness &&
        (Options.ExtensionIdFor(connection.Browser) is not { } id || string.Equals(connection.ExtensionId, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>SIDs are compared without case (WFP, the registry and the token may spell them differently).</summary>
    private static AccountBrowser Key(string accountId, BrowserKind browser) => new(accountId.ToUpperInvariant(), browser);

    private sealed class Watch(DateTimeOffset runningSince)
    {
        public DateTimeOffset RunningSince { get; set; } = runningSince;
        public HashSet<(int ProcessId, DateTimeOffset StartedAt)> Processes { get; set; } = [];
        public bool Blocked { get; set; }
    }
}

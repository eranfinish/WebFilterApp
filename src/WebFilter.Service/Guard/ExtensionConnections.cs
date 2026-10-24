using WebFilter.Core.Engine;

namespace WebFilter.Service.Guard;

/// <summary>
/// The extension's sessions on the agent pipe, for the extension guard (PROTECTION-SPEC §14): which account has one (from the
/// pipe client's token — never from the message), from which browser and extension id (from its hello), and when it was
/// last heard from (any message: the heartbeat comes every 30 seconds). A closed session keeps the time of its last message
/// for <see cref="ClosedKept"/>, so an extension that reloads — an update, a restarted service worker or native host — still
/// counts while it reconnects. Thread-safe.
/// </summary>
public sealed class ExtensionConnections
{
    /// <summary>How long a closed session is remembered; longer than the guard's staleness limit (90 seconds).</summary>
    public static readonly TimeSpan ClosedKept = TimeSpan.FromMinutes(10);

    /// <summary>An extension id is 32 letters; anything longer is cut, so a client cannot make us keep a large string.</summary>
    private const int MaxIdLength = 64;

    private readonly object _gate = new();
    private readonly List<Session> _sessions = [];

    /// <summary>Raised (outside the lock) when a session says hello: a browser the guard blocks can be restored at once.</summary>
    public event Action? Connected;

    /// <summary>A session said hello as <paramref name="browser"/> with <paramref name="extensionId"/>, for the account <paramref name="accountId"/>.</summary>
    public Session Open(string accountId, BrowserKind browser, string? extensionId, DateTimeOffset now)
    {
        var session = new Session(this, accountId, browser, extensionId is { Length: > MaxIdLength } ? extensionId[..MaxIdLength] : extensionId, now);
        lock (_gate)
        {
            Prune(now);
            _sessions.Add(session);
        }
        Connected?.Invoke();
        return session;
    }

    /// <summary>The sessions open now and those closed within <see cref="ClosedKept"/>, each with when it was last heard from.</summary>
    public IReadOnlyList<ExtensionConnection> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            Prune(now);
            return _sessions.Select(s => new ExtensionConnection(s.AccountId, s.Browser, s.ExtensionId, s.LastHeard)).ToList();
        }
    }

    private void Prune(DateTimeOffset now) => _sessions.RemoveAll(s => s.Closed && now - s.LastHeard > ClosedKept);

    /// <summary>One session, from its hello until the pipe closes.</summary>
    public sealed class Session
    {
        private readonly ExtensionConnections _owner;

        internal Session(ExtensionConnections owner, string accountId, BrowserKind browser, string? extensionId, DateTimeOffset now)
        {
            _owner = owner;
            (AccountId, Browser, ExtensionId, LastHeard) = (accountId, browser, extensionId, now);
        }

        public string AccountId { get; }
        public BrowserKind Browser { get; }
        public string? ExtensionId { get; }
        public DateTimeOffset LastHeard { get; private set; }
        public bool Closed { get; private set; }

        /// <summary>A message arrived on the session.</summary>
        public void Heard(DateTimeOffset now)
        {
            lock (_owner._gate)
                if (!Closed && now > LastHeard) LastHeard = now;
        }

        /// <summary>The pipe closed: the session stops being heard from, and its last message ages out.</summary>
        public void Close()
        {
            lock (_owner._gate) Closed = true;
        }
    }
}

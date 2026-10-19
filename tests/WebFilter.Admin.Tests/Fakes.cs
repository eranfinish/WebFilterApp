using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;

namespace WebFilter.Admin.Tests;

internal static class TestSetup
{
    /// <summary>The app pins Hebrew at startup; tests do the same so wording does not depend on the machine's language.</summary>
    [ModuleInitializer]
    internal static void PinHebrew()
    {
        var hebrew = CultureInfo.GetCultureInfo("he-IL");
        CultureInfo.DefaultThreadCurrentCulture = hebrew;
        CultureInfo.DefaultThreadCurrentUICulture = hebrew;
        Strings.Culture = hebrew;
    }
}

/// <summary>
/// An in-memory admin service: answers queries from its properties, records every request, and returns
/// <see cref="AdminOk"/> for commands. Completes synchronously, so view-model calls finish before they return.
/// </summary>
internal sealed class FakeAdminClient : IAdminClient
{
    private AdminConnectionState _state = AdminConnectionState.Connected;

    public string PipeName { get; set; } = "WebFilter.Test.Admin";

    public AdminConnectionState State
    {
        get => _state;
        set
        {
            _state = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? StateChanged;

    public List<AdminMessage> Sent { get; } = [];

    public FamilyBundle Settings { get; set; } = TestData.Family();

    public StatusResult Status { get; set; } = TestData.Status();

    public List<RequestDto> Requests { get; } = [];

    public List<EventDto> Events { get; } = [];

    /// <summary>Overrides the reply for a request (return null to fall back to the default).</summary>
    public Func<AdminMessage, AdminMessage?>? Reply { get; set; }

    /// <summary>When set, every request throws it (e.g. a connection failure).</summary>
    public Exception? Failure { get; set; }

    public IEnumerable<T> SentOf<T>() where T : AdminMessage => Sent.OfType<T>();

    public T Single<T>() where T : AdminMessage => Assert.Single(Sent.OfType<T>());

    public Task<AdminMessage> SendAsync(AdminMessage request, CancellationToken ct = default)
    {
        Sent.Add(request);
        if (Failure is not null) return Task.FromException<AdminMessage>(Failure);
        var reply = Reply?.Invoke(request) ?? request switch
        {
            GetStatus => Status,
            GetSettings => new SettingsResult(Settings),
            GetRequests q => new RequestsResult([.. Requests.Where(r => q.Status is null || r.Status == q.Status)]),
            GetEvents => new EventsResult([.. Events]),
            _ => (AdminMessage)new AdminOk(),
        };
        return Task.FromResult(reply);
    }
}

internal sealed class ManualTimer : IUiTimer
{
    public TimeSpan? Interval { get; private set; }

    public Func<Task>? Tick { get; private set; }

    public bool Stopped { get; private set; }

    public IDisposable Start(TimeSpan interval, Func<Task> tick)
    {
        Interval = interval;
        Tick = tick;
        return new Stopper(this);
    }

    public Task FireAsync() => Tick?.Invoke() ?? Task.CompletedTask;

    private sealed class Stopper(ManualTimer timer) : IDisposable
    {
        public void Dispose() => timer.Stopped = true;
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public bool Answer { get; set; } = true;

    public List<string> Asked { get; } = [];

    public bool Confirm(string message, string title)
    {
        Asked.Add(message);
        return Answer;
    }
}

internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal static class Bidi
{
    /// <summary>The text without the invisible direction marks the UI adds around left-to-right values.</summary>
    public static string? Plain(string? text) => text?.Replace("‎", "", StringComparison.Ordinal).Replace("‏", "", StringComparison.Ordinal);
}

internal static class TestData
{
    /// <summary>Monday 19 Oct 2026, 16:00 in Israel (UTC+3, before the switch to winter time).</summary>
    public static readonly DateTimeOffset Now = new(2026, 10, 19, 16, 0, 0, TimeSpan.FromHours(3));

    /// <summary>tests/simulation/fixtures/family-base.json: noa (child, allowlist), yoav (teen, blocklist), dana and savta (adults, unrestricted).</summary>
    public static FamilyBundle Family(int version = 7) =>
        FamilyBundleJson.Deserialize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "family-base.json"))) with { Version = version };

    public static StatusResult Status(int version = 7, params MemberStatus[] members) =>
        new(version, "pc-parents", true, Now, members.Length > 0 ? members : DefaultMembers());

    public static MemberStatus[] DefaultMembers() =>
    [
        new("noa", "נועה", "ok", 45, 120, 75, "19:00", null, null),
        new("yoav", "יואב", "expired", 240, 240, 0, "21:30", null, null),
        new("dana", "דנה", "unlimited", 0, null, null, null, null, null),
        new("savta", "סבתא", "unlimited", 0, null, null, null, null, null),
    ];

    public static RequestDto Request(string id, string kind, string target, string targetKey, int? minutes = null, string status = "pending",
        string member = "noa", DateTimeOffset? created = null, DateTimeOffset? decided = null, string? message = null) =>
        new(id, member, "pc-kids", kind, target, targetKey, minutes, message, created ?? Now.AddMinutes(-5), status, decided);
}

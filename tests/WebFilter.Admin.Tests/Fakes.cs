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

    /// <summary>The hub's paired computers. <see cref="RevokeDevice"/> marks one revoked, as the service does.</summary>
    public List<DeviceInfo> Devices { get; } = [];

    /// <summary>
    /// False for an agent computer, answering like the service there: the status (with <see cref="AgentHub"/> and the family's
    /// zone) and the local pause work; a revoke is not_found; everything else is not_hub.
    /// </summary>
    public bool IsHub { get; set; } = true;

    /// <summary>On an agent: the hub that manages it. Reachable, last heard a minute before <see cref="TestData.Now"/>.</summary>
    public HubConnection AgentHub { get; set; } = new("pc-parents", "192.168.1.20:47800", true, TestData.Now.AddMinutes(-1));

    /// <summary>The next code <see cref="CreatePairingCode"/> returns; it expires 10 minutes after <see cref="StatusResult.Now"/>.</summary>
    public string PairingCode { get; set; } = TestData.PairingCode;

    public List<string> PairingAddresses { get; } = [.. TestData.HubAddresses];

    /// <summary>What <see cref="ScanAccounts"/> finds on this computer: added (unlinked) to the hub device when not known yet.</summary>
    public List<AccountDto> LocalAccounts { get; } = [];

    /// <summary>What <see cref="GetDiagnostics"/> answers (a development run on a private network, by default).</summary>
    public List<CheckResult> Checks { get; } =
    [
        new("service", "warn"), new("browsers", "ok", "edge,chrome"), new("browser_policies", "warn"), new("extension", "warn"), new("network", "ok"),
    ];

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
        var reply = Reply?.Invoke(request) ?? (IsHub ? null : AsAgent(request)) ?? request switch
        {
            GetStatus => Status with { TimeZone = Settings.TimeZone },
            GetSettings => new SettingsResult(Settings),
            GetRequests q => new RequestsResult([.. Requests.Where(r => q.Status is null || r.Status == q.Status)]),
            GetEvents => new EventsResult([.. Events]),
            GetDevices => new DevicesResult([.. Devices]),
            CreatePairingCode => new PairingCodeResult(PairingCode, Status.Now + TimeSpan.FromMinutes(10), TestData.HubPort, [.. PairingAddresses]),
            RevokeDevice revoke => Revoke(revoke.DeviceId),
            ReplaceSettings replace => Replace(replace),
            ScanAccounts => Scan(),
            GetDiagnostics => new DiagnosticsResult([.. Checks]),
            SetNetworksPrivate => MakeNetworksPrivate(),
            _ => (AdminMessage)new AdminOk(),
        };
        return Task.FromResult(reply);
    }


    /// <summary>Like the service: every Public network (the "network" check's detail) becomes Private.</summary>
    private AdminOk MakeNetworksPrivate()
    {
        var index = Checks.FindIndex(c => c.Id == "network" && c.State == "warn");
        if (index < 0) return new AdminOk("");
        var names = Checks[index].Detail ?? "";
        Checks[index] = new CheckResult("network", "ok");
        return new AdminOk(names);
    }
    /// <summary>The agent's admin pipe (AdminPipeServer.HandleOnAgent): null for the messages it answers like the hub.</summary>
    private AdminMessage? AsAgent(AdminMessage request) => request switch
    {
        GetStatus => Status with { IsHub = false, TimeZone = Settings.TimeZone, Hub = AgentHub },
        Pause or SetClock or GetDiagnostics => null,
        RevokeDevice => new AdminError("not_found", "No such paired device."),
        _ => new AdminError("not_hub", "This computer is managed by the hub computer; make this change there."),
    };

    /// <summary>Like the service: refused when the settings moved past <see cref="ReplaceSettings.IfVersion"/>; otherwise taken, with a new version.</summary>
    private AdminMessage Replace(ReplaceSettings replace)
    {
        if (replace.IfVersion is { } expected && expected != Settings.Version)
            return new AdminError(ReplaceSettings.ConflictCode, $"The settings changed meanwhile (version {Settings.Version}, expected {expected}).");
        BumpVersion(replace.Settings);
        return new AdminOk();
    }

    private AdminMessage Scan()
    {
        var device = Settings.Devices.Single(d => d.Id == Status.DeviceId);
        var added = LocalAccounts.Where(a => device.Accounts.All(known => known.AccountId != a.AccountId)).Select(a => a with { Member = null }).ToList();
        if (added.Count > 0)
            BumpVersion(Settings with { Devices = [.. Settings.Devices.Select(d => d.Id == device.Id ? d with { Accounts = [.. d.Accounts, .. added] } : d)] });
        return new AdminOk(added.Count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Stores new settings at the next version, and reports it in the status, as every change does on the service.</summary>
    public void BumpVersion(FamilyBundle? settings = null)
    {
        var version = Settings.Version + 1;
        Settings = (settings ?? Settings) with { Version = version };
        Status = Status with { SettingsVersion = version };
    }

    private AdminMessage Revoke(string deviceId)
    {
        var index = Devices.FindIndex(d => d.Id == deviceId && !d.Revoked);
        if (index < 0) return new AdminError("not_found", "No such paired device.");
        Devices[index] = Devices[index] with { Revoked = true };
        return new AdminOk();
    }
}

/// <summary>Timers that fire only when a test says so. The first one started is the main window's refresh tick.</summary>
internal sealed class ManualTimer : IUiTimer
{
    private readonly List<Handle> _timers = [];

    public TimeSpan? Interval => _timers.FirstOrDefault()?.Interval;

    public bool Stopped => _timers.FirstOrDefault()?.Stopped ?? false;

    /// <summary>The intervals of the timers started and not stopped yet.</summary>
    public IReadOnlyList<TimeSpan> Running => [.. _timers.Where(t => !t.Stopped).Select(t => t.Interval)];

    public IDisposable Start(TimeSpan interval, Func<Task> tick)
    {
        var timer = new Handle(interval, tick);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Fires the first timer started, unless it was stopped.</summary>
    public Task FireAsync() => _timers.FirstOrDefault() is { Stopped: false } first ? first.Tick() : Task.CompletedTask;

    /// <summary>Fires every running timer with this interval.</summary>
    public async Task FireAsync(TimeSpan interval)
    {
        foreach (var timer in _timers.Where(t => !t.Stopped && t.Interval == interval).ToList()) await timer.Tick();
    }

    private sealed class Handle(TimeSpan interval, Func<Task> tick) : IDisposable
    {
        public TimeSpan Interval { get; } = interval;

        public Func<Task> Tick { get; } = tick;

        public bool Stopped { get; private set; }

        public void Dispose() => Stopped = true;
    }
}

internal sealed class FakeDialogs : IDialogService
{
    public bool Answer { get; set; } = true;

    public List<string> Asked { get; } = [];

    public List<string> Titles { get; } = [];

    public bool Confirm(string message, string title)
    {
        Asked.Add(message);
        Titles.Add(title);
        return Answer;
    }
}

internal sealed class FakeClipboard : IClipboardService
{
    /// <summary>False while "another program" holds the clipboard.</summary>
    public bool Available { get; set; } = true;

    public string? Text { get; private set; }

    public bool TrySetText(string text)
    {
        if (!Available) return false;
        Text = text;
        return true;
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

    public const string PairingCode = "K7QM-2RXD-9HBA";

    public const int HubPort = 47800;

    /// <summary>The home network first, then a Hyper-V virtual switch.</summary>
    public static readonly string[] HubAddresses = ["192.168.1.20", "172.24.48.1"];

    /// <summary>Two kids' computers (one connected now), one that never connected yet, and one revoked.</summary>
    public static DeviceInfo[] Devices() =>
    [
        new("pc-yoav", "YOAV-LAPTOP", "windows", Now.AddDays(-12), Now.AddMinutes(-42), Revoked: false),
        new("pc-kids", "KIDS-PC", "windows", new DateTimeOffset(2025, 12, 1, 18, 30, 0, TimeSpan.FromHours(2)), Now.AddSeconds(-15), Revoked: false),
        new("pc-old", "OLD-DESKTOP", "windows", Now.AddDays(-400), Now.AddDays(-30), Revoked: true),
        new("pc-basement", "basement", "windows", Now.AddMinutes(-3), LastSeenAt: null, Revoked: false),
    ];

    public static RequestDto Request(string id, string kind, string target, string targetKey, int? minutes = null, string status = "pending",
        string member = "noa", DateTimeOffset? created = null, DateTimeOffset? decided = null, string? message = null) =>
        new(id, member, "pc-kids", kind, target, targetKey, minutes, message, created ?? Now.AddMinutes(-5), status, decided);
}

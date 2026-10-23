using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Contracts.Notify;
using WebFilter.Core.Engine;
using WebFilter.Core.Model;
using WebFilter.Service.Storage;

namespace WebFilter.Service.Tests;

/// <summary>
/// PROTECTION-SPEC §10 on the hub without pipes: computers that stop reporting, new Windows accounts, the tamper
/// throttle, and event retention (search terms are events, so they leave after the same 90 days). The pipe itself is
/// covered in PipeIntegrationTests.
/// </summary>
public sealed class HubNotificationTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-02T08:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-notify-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new(Start);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <param name="fixture">The family of the simulation fixture; otherwise a new install's empty family.</param>
    private FamilyService Hub(bool fixture = true)
    {
        var family = new FamilyService(Options.Create(new ServiceOptions
        {
            Role = "hub", DeviceId = "pc-parents", DataDirectory = Path.Combine(_dir, "data"), ProfileDirectory = Path.Combine(_dir, "profiles"),
            BootstrapBundlePath = fixture ? Path.Combine(RepoRoot(), "tests", "simulation", "fixtures", "family-base.json") : null,
        }), _clock, NullLogger<FamilyService>.Instance);
        family.Start();
        return family;
    }

    [Fact]
    public void A_new_standard_account_on_a_paired_computer_is_one_notification_but_not_what_it_reports_first()
    {
        using var hub = Hub();
        hub.RegisterPairedDevice("pc-new", "NEW-PC", "windows", "CC");

        // Its first report: the accounts it already had when it was paired, which the parent goes through then.
        hub.IngestUpload("pc-new", Upload(("S-1-5-21-4000-500", "owner", true), ("S-1-5-21-4000-1001", "maya", false)));
        Assert.Empty(NewAccounts(hub));

        // Later a parent creates a standard account for a child, and an administrator (never filtered: no notification).
        _clock.Now = Start.AddMinutes(5);
        hub.IngestUpload("pc-new", Upload(("S-1-5-21-4000-500", "owner", true), ("S-1-5-21-4000-1001", "maya", false),
            ("S-1-5-21-4000-1002", "tom", false), ("S-1-5-21-4000-1003", "grandpa", true)));
        var account = Assert.Single(NewAccounts(hub));
        Assert.Equal(("account:pc-new:S-1-5-21-4000-1002", "pc-new", null, "tom", Start.AddMinutes(5)),
            (account.Id, account.DeviceId, account.MemberId, account.Detail, account.At));

        // Reported again, renamed, or dropped from the settings and reported anew: still the one notification.
        hub.IngestUpload("pc-new", Upload(("S-1-5-21-4000-1002", "tommy", false)));
        hub.WithHub((core, now) =>
        {
            var config = core.Config.Clone();
            var index = config.Devices.FindIndex(d => d.Id == "pc-new");
            config.Devices[index] = config.Devices[index] with { Accounts = [.. config.Devices[index].Accounts.Where(a => a.Name != "tommy")] };
            core.ReplaceSettings(config, now);
            return 0;
        });
        hub.IngestUpload("pc-new", Upload(("S-1-5-21-4000-1002", "tommy", false)));
        Assert.Contains(hub.WithHub((core, _) => core.Config.Device("pc-new").Accounts.ToList()), a => a.Name == "tommy");
        Assert.Single(NewAccounts(hub));
    }

    [Fact]
    public void The_hubs_own_accounts_at_first_setup_are_no_notification_and_one_created_later_is()
    {
        using var hub = Hub(fixture: false);
        var accounts = new FakeLocalAccounts(
            new DeviceAccount("S-1-5-21-1000-1001", "dana", null, IsAdmin: true),
            new DeviceAccount("S-1-5-21-1000-1002", "kids", null, IsAdmin: false));
        var admin = new Pipes.AdminPipeServer(hub, Options.Create(hub.Options), _clock, NullLogger<Pipes.AdminPipeServer>.Instance, accounts);

        // The service's own first look at this computer's accounts (or the wizard's): what the computer already had.
        Assert.Equal("2", Assert.IsType<Contracts.Admin.AdminOk>(admin.Handle(new Contracts.Admin.ScanAccounts(), "dana")).Detail);
        Assert.Empty(NewAccounts(hub));

        accounts.Add(new DeviceAccount("S-1-5-21-1000-1003", "noa", null, IsAdmin: false));
        accounts.Add(new DeviceAccount("S-1-5-21-1000-1004", "avi", null, IsAdmin: true));
        Assert.Equal("2", Assert.IsType<Contracts.Admin.AdminOk>(admin.Handle(new Contracts.Admin.ScanAccounts(), "dana")).Detail);

        var account = Assert.Single(NewAccounts(hub));
        Assert.Equal(("account:pc-parents:S-1-5-21-1000-1003", "pc-parents", "noa"), (account.Id, account.DeviceId, account.Detail));
    }

    [Fact]
    public void A_computer_that_stops_reporting_for_a_day_is_one_notification_per_silence()
    {
        using var hub = Hub();
        hub.Devices!.Register("laptop-yoav", "Yoav's laptop", "windows", "AA", Start);
        hub.Devices.Register("pc-revoked", "Old PC", "windows", "BB", Start);
        hub.Devices.Revoke("pc-revoked", Start);
        hub.Devices.Touch("laptop-yoav", Start.AddHours(1));

        _clock.Now = Start.AddHours(24);
        hub.Tick();
        Assert.Empty(Devices(hub)); // 23 hours since it last reported

        _clock.Now = Start.AddHours(26);
        hub.Tick();
        hub.Tick();
        var silent = Assert.Single(Devices(hub));
        Assert.Equal(("laptop-yoav", null, Start.AddHours(1)), (silent.DeviceId, silent.MemberId, DateTimeOffset.Parse(silent.Detail!, System.Globalization.CultureInfo.InvariantCulture)));

        // It reports again, then falls silent again: a new notification.
        hub.Devices.Touch("laptop-yoav", Start.AddHours(27));
        _clock.Now = Start.AddHours(52);
        hub.Tick();
        Assert.Equal(2, Devices(hub).Count);
    }

    [Fact]
    public void A_repeated_tamper_is_reported_once_an_hour()
    {
        using var hub = Hub();
        hub.ReportTamper("S-1-5-21-1000-1001", TamperKinds.PolicyRepaired);
        hub.ReportTamper("S-1-5-21-1000-1001", TamperKinds.PolicyRepaired);
        hub.ReportTamper(null, TamperKinds.PolicyRepaired); // the machine-wide values: another subject
        Assert.Equal(2, Tamper(hub).Count);

        _clock.Now = Start.AddMinutes(61);
        hub.ReportTamper("S-1-5-21-1000-1001", TamperKinds.PolicyRepaired);
        Assert.Equal(3, Tamper(hub).Count);
        Assert.Equal(3, hub.NotificationsSince(null).Count(n => n.Kind == NotificationKinds.Tamper));
    }

    [Fact]
    public void Events_past_retention_leave_memory_and_the_database_daily_searches_included()
    {
        using (var hub = Hub())
        {
            hub.WithHub((core, now) =>
            {
                core.Events.Add(new FamilyEvent(EventType.Search, now.AddDays(-91), "noa", "pc-kids", Url: "https://www.google.com/search?q=old", Detail: "google:old"));
                core.Events.Add(new FamilyEvent(EventType.Search, now.AddDays(-89), "noa", "pc-kids", Url: "https://www.google.com/search?q=recent", Detail: "google:recent"));
                return 0;
            });
        }

        // Retention runs at start, so a restarted hub no longer has the old search.
        using var again = Hub();
        var searches = again.WithHub((core, _) => core.Events.Where(e => e.Type == EventType.Search).Select(e => e.Detail).ToList());
        Assert.Equal(["google:recent"], searches);

        // And once a day while it runs.
        _clock.Now = Start.AddDays(2);
        again.Tick();
        Assert.Empty(again.WithHub((core, _) => core.Events.Where(e => e.Type == EventType.Search).ToList()));
        again.Flush();
        Assert.DoesNotContain(new HubStore(new SqliteDatabase(Path.Combine(_dir, "data", "hub.db"), "hub")).Load()!.Events, e => e.Type == EventType.Search);
    }

    private static List<NotificationDto> Devices(FamilyService hub) => hub.NotificationsSince(null).Where(n => n.Kind == NotificationKinds.Device).ToList();

    private static List<NotificationDto> NewAccounts(FamilyService hub) => hub.NotificationsSince(null).Where(n => n.Kind == NotificationKinds.Account).ToList();

    /// <summary>An agent's upload with only its accounts, as its <c>HubSyncService</c> sends it when they change.</summary>
    private static Contracts.Hub.UploadDto Upload(params (string Id, string Name, bool IsAdmin)[] accounts) =>
        new("ignored", [], [], [], [.. accounts.Select(a => new Contracts.Hub.AccountDto(a.Id, a.Name, null, a.IsAdmin))]);

    private static List<FamilyEvent> Tamper(FamilyService hub) => hub.WithHub((core, _) => core.Events.Where(e => e.Type == EventType.Tamper).ToList());

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx"))) return dir.FullName;
        throw new InvalidOperationException("WebFilter.slnx not found");
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

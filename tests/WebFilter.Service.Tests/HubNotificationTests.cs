using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Contracts.Notify;
using WebFilter.Core.Engine;
using WebFilter.Service.Storage;

namespace WebFilter.Service.Tests;

/// <summary>
/// PROTECTION-SPEC §10 on the hub without pipes: computers that stop reporting, the tamper throttle, and event retention
/// (search terms are events, so they leave after the same 90 days). The pipe itself is covered in PipeIntegrationTests.
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

    private FamilyService Hub()
    {
        var family = new FamilyService(Options.Create(new ServiceOptions
        {
            Role = "hub", DeviceId = "pc-parents", DataDirectory = Path.Combine(_dir, "data"), ProfileDirectory = Path.Combine(_dir, "profiles"),
            BootstrapBundlePath = Path.Combine(RepoRoot(), "tests", "simulation", "fixtures", "family-base.json"),
        }), _clock, NullLogger<FamilyService>.Instance);
        family.Start();
        return family;
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

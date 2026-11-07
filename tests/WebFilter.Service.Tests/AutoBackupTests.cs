using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebFilter.Contracts.Admin;
using WebFilter.Contracts.Hub;
using WebFilter.Service.Hub;
using WebFilter.Service.Mapping;

namespace WebFilter.Service.Tests;

/// <summary>
/// The hub's automatic backups into a folder a parent chose: written when the family's settings change (not when only the
/// version moves), the latest file and one copy a day kept for 30 days, folders that cannot work refused with nothing
/// changed, failures kept for the admin app and recovered from, and the folder remembered across restarts.
/// </summary>
public sealed class AutoBackupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-autobackup-" + Guid.NewGuid().ToString("N"));
    private readonly string _folder;
    private readonly ManualClock _clock = new(DateTimeOffset.Parse("2026-11-07T10:00:00+02:00"));
    private readonly FamilyService _hub;

    public AutoBackupTests()
    {
        _folder = Path.Combine(_dir, "OneDrive", "WebFilter");
        Directory.CreateDirectory(_folder);
        var fixture = Path.Combine(RepoRoot(), "tests", "simulation", "fixtures", "family-base.json");
        _hub = new FamilyService(Options.Create(new ServiceOptions
        {
            Role = "hub", DeviceId = "pc-parents", DataDirectory = Path.Combine(_dir, "data"), ProfileDirectory = Path.Combine(_dir, "profiles"),
            BootstrapBundlePath = fixture,
        }), _clock, NullLogger<FamilyService>.Instance);
        _hub.Start();
    }

    public void Dispose()
    {
        _hub.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private AutoBackup NewBackup() => new(_hub, NullLogger<AutoBackup>.Instance);

    private string Latest => Path.Combine(_folder, AutoBackup.LatestFileName);

    private void RenameMember(string name) => _hub.WithHub((hub, now) =>
    {
        var bundle = BundleMapper.ToBundle(hub.Config, hub.Version);
        hub.ReplaceSettings(BundleMapper.ToConfig(bundle with { Members = [.. bundle.Members.Select(m => m.Id == "noa" ? m with { DisplayName = name } : m)] }), now);
        return 0;
    });

    private void BumpVersionOnly() => _hub.WithHub((hub, now) =>
    {
        hub.ReplaceSettings(BundleMapper.ToConfig(BundleMapper.ToBundle(hub.Config, hub.Version)), now);
        return 0;
    });

    [Fact]
    public void Choosing_a_folder_writes_a_backup_there_at_once()
    {
        var backup = NewBackup();

        Assert.Null(backup.Configure(_folder));

        var file = FamilyBackupFormat.Read(File.ReadAllText(Latest));
        Assert.Equal("pc-parents", file.HubDeviceId);
        Assert.Equal(_hub.WithHub((hub, _) => FamilyBundleJson.Serialize(BundleMapper.ToBundle(hub.Config, hub.Version))), FamilyBundleJson.Serialize(file.Settings));
        Assert.True(File.Exists(Path.Combine(_folder, "WebFilter-backup-2026-11-07.json")));
        Assert.Equal(new AutoBackupResult(_folder, _clock.GetUtcNow(), null), backup.Status());
    }

    [Fact]
    public void A_change_to_the_settings_is_saved_but_a_version_alone_is_not()
    {
        var backup = NewBackup();
        backup.Configure(_folder);
        var written = File.GetLastWriteTimeUtc(Latest);

        BumpVersionOnly();
        Assert.False(backup.RunIfChanged());
        Assert.Equal(written, File.GetLastWriteTimeUtc(Latest));

        RenameMember("Noa renamed");
        Assert.True(backup.RunIfChanged());
        Assert.Contains(FamilyBackupFormat.Read(File.ReadAllText(Latest)).Settings.Members, m => m.DisplayName == "Noa renamed");
        Assert.False(backup.RunIfChanged());
    }

    [Fact]
    public void A_deleted_latest_file_is_written_again()
    {
        var backup = NewBackup();
        backup.Configure(_folder);
        File.Delete(Latest);

        Assert.True(backup.RunIfChanged());
        Assert.True(File.Exists(Latest));
    }

    [Fact]
    public void Daily_copies_older_than_30_days_go_and_other_files_stay()
    {
        File.WriteAllText(Path.Combine(_folder, "WebFilter-backup-2026-09-01.json"), "old");
        File.WriteAllText(Path.Combine(_folder, "WebFilter-backup-2026-10-20.json"), "recent");
        File.WriteAllText(Path.Combine(_folder, "WebFilter-backup-notes.json"), "a parent's own file");
        File.WriteAllText(Path.Combine(_folder, "photo.jpg"), "x");

        NewBackup().Configure(_folder);

        Assert.Equal(["WebFilter-backup-2026-10-20.json", "WebFilter-backup-2026-11-07.json", "WebFilter-backup-latest.json", "WebFilter-backup-notes.json", "photo.jpg"],
            Directory.GetFiles(_folder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_new_day_gets_its_own_copy()
    {
        var backup = NewBackup();
        backup.Configure(_folder);
        _clock.Advance(TimeSpan.FromDays(1));

        RenameMember("Noa tomorrow");
        Assert.True(backup.RunIfChanged());

        Assert.True(File.Exists(Path.Combine(_folder, "WebFilter-backup-2026-11-07.json")));
        Assert.True(File.Exists(Path.Combine(_folder, "WebFilter-backup-2026-11-08.json")));
    }

    [Theory]
    [InlineData("backups", SetAutoBackup.InvalidCode)]
    [InlineData(@"\\nas\family\WebFilter", SetAutoBackup.NetworkCode)]
    [InlineData("//nas/family", SetAutoBackup.NetworkCode)]
    public void Folders_that_cannot_work_are_refused_and_nothing_changes(string folder, string code)
    {
        var backup = NewBackup();

        Assert.Equal(code, backup.Configure(folder)?.Code);
        Assert.Null(backup.Status().Folder);
    }

    [Fact]
    public void A_missing_folder_is_refused()
    {
        var backup = NewBackup();
        backup.Configure(_folder);

        Assert.Equal(SetAutoBackup.MissingCode, backup.Configure(Path.Combine(_dir, "nowhere"))?.Code);
        Assert.Equal(_folder, backup.Status().Folder); // the previous folder stays
    }

    [Fact]
    public void A_failure_is_kept_for_the_parents_and_the_next_attempt_recovers()
    {
        var backup = NewBackup();
        backup.Configure(_folder);
        Directory.Delete(_folder, recursive: true);

        RenameMember("Noa while the folder is gone");
        Assert.False(backup.RunIfChanged());
        Assert.NotNull(backup.Status().LastError);

        Directory.CreateDirectory(_folder);
        Assert.True(backup.RunIfChanged());
        Assert.Null(backup.Status().LastError);
        Assert.Contains(FamilyBackupFormat.Read(File.ReadAllText(Latest)).Settings.Members, m => m.DisplayName == "Noa while the folder is gone");
    }

    [Fact]
    public void Turned_off_it_writes_nothing_and_the_folder_is_remembered_across_restarts()
    {
        NewBackup().Configure(_folder);
        var restarted = NewBackup();
        Assert.Equal(_folder, restarted.Status().Folder);

        Assert.Null(restarted.Configure(null));
        File.Delete(Latest);
        RenameMember("Noa after turning off");

        Assert.False(restarted.RunIfChanged());
        Assert.False(File.Exists(Latest));
        Assert.Null(NewBackup().Status().Folder);
    }

    [Fact]
    public void The_admin_pipe_sets_and_reports_the_folder_on_the_hub_only()
    {
        var backup = NewBackup();
        var admin = new Pipes.AdminPipeServer(_hub, Options.Create(_hub.Options), _clock, NullLogger<Pipes.AdminPipeServer>.Instance, backup: backup);

        Assert.IsType<AdminOk>(admin.Handle(new SetAutoBackup(_folder), "dad"));
        var status = Assert.IsType<AutoBackupResult>(admin.Handle(new GetAutoBackup(), "dad"));
        Assert.Equal(_folder, status.Folder);
        Assert.Equal(SetAutoBackup.InvalidCode, Assert.IsType<AdminError>(admin.Handle(new SetAutoBackup("relative"), "dad")).Code);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WebFilter.slnx"))) return dir.FullName;
        throw new InvalidOperationException("WebFilter.slnx not found");
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

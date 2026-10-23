using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WebFilter.Notifier.Tests;

/// <summary>The cursor and log files, the admin app launch and the one-per-session rule.</summary>
public sealed class NotifierFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wf-notifier-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void The_cursor_file_keeps_the_exact_instant_and_its_folder_is_created()
    {
        var store = new FileCursorStore(Path.Combine(_dir, "WebFilter", "notifier-cursor.txt"));
        Assert.Null(store.Load());

        var at = new DateTimeOffset(2026, 10, 22, 17, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        store.Save(at);
        store.Save(at.AddTicks(1));

        Assert.Equal(at.AddTicks(1), store.Load());
        Assert.Equal(["notifier-cursor.txt"], Directory.GetFiles(Path.Combine(_dir, "WebFilter")).Select(Path.GetFileName));
    }

    [Fact]
    public void A_cursor_file_that_holds_no_time_is_reported()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "notifier-cursor.txt");
        File.WriteAllText(path, "yesterday");

        Assert.Throws<InvalidDataException>(() => new FileCursorStore(path).Load());
    }

    [Fact]
    public void The_log_appends_lines_and_starts_again_when_it_grows_too_big()
    {
        var path = Path.Combine(_dir, "notifier.log");
        var log = new FileNotifierLog(path, new ManualTime(TestData.Now));

        log.Write("first");
        log.Write("second");
        Assert.Equal(2, File.ReadAllLines(path).Length);
        Assert.EndsWith(" first", File.ReadAllLines(path)[0], StringComparison.Ordinal);

        log.Write(new string('x', (int)FileNotifierLog.MaxBytes));
        log.Write("third");

        Assert.Equal(["third"], File.ReadAllLines(path).Select(l => l[(l.LastIndexOf(' ') + 1)..]));
        Assert.True(File.Exists(path + ".old"));
    }

    [Fact]
    public void Opening_the_admin_app_goes_through_the_shell_so_Windows_asks_for_elevation()
    {
        var started = new List<ProcessStartInfo>();
        var log = new ListLog();
        var launcher = new AdminLauncher(@"C:\Program Files\WebFilter\WebFilter.Admin.exe", info =>
        {
            started.Add(info);
            return null;
        }, log);

        Assert.True(launcher.Open());

        var info = Assert.Single(started);
        Assert.Equal(@"C:\Program Files\WebFilter\WebFilter.Admin.exe", info.FileName);
        Assert.True(info.UseShellExecute);
        Assert.Equal("", info.Verb); // the admin app's manifest asks for the rights; the notifier adds nothing
        Assert.Equal(@"C:\Program Files\WebFilter", info.WorkingDirectory);
        Assert.Empty(log.Lines);
        Assert.Equal("WebFilter.Admin.exe", Path.GetFileName(AdminLauncher.DefaultPath));
    }

    [Fact]
    public void A_declined_prompt_is_not_an_error_but_a_missing_app_is_logged()
    {
        var log = new ListLog();

        Assert.False(new AdminLauncher("admin.exe", _ => throw new Win32Exception(1223), log).Open());
        Assert.Empty(log.Lines);

        Assert.False(new AdminLauncher("admin.exe", _ => throw new Win32Exception(2), log).Open());
        Assert.StartsWith("The admin app admin.exe could not be started", Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void Only_one_notifier_runs_in_a_session()
    {
        var name = @"Local\WebFilter.Notifier.Test." + Guid.NewGuid().ToString("N");

        using (var first = SingleInstance.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstance.TryAcquire(name));
        }

        // Once the first one has exited, the next start runs.
        using var again = SingleInstance.TryAcquire(name);
        Assert.NotNull(again);
    }
}

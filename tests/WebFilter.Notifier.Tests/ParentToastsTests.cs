using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier.Tests;

/// <summary>
/// What the tray does with toasts: one per poll, registering for clicks only when it matters, a click (on Windows' thread)
/// opening the admin app on the tray's, and whatever Windows refuses logged rather than thrown. Windows is a fake here:
/// nothing registers anything with the real one.
/// </summary>
public sealed partial class ParentToastsTests
{
    private const string AdminPath = @"C:\Program Files\WebFilter\WebFilter.Admin.exe";

    private readonly FakeToastPlatform _windows = new();
    private readonly ListLog _log = new();
    private readonly List<ProcessStartInfo> _started = [];
    private readonly Queue<Action> _posted = new();
    private readonly ParentToasts _toasts;

    public ParentToastsTests()
    {
        var admin = new AdminLauncher(AdminPath, info =>
        {
            _started.Add(info);
            return null;
        }, _log);
        _toasts = new ParentToasts(_windows, admin, _log, _posted.Enqueue);
    }

    /// <summary>Runs what the clicks posted to the tray's thread.</summary>
    private void RunPosted()
    {
        while (_posted.TryDequeue(out var action)) action();
    }

    private void OpenedTheAdminAppOnce()
    {
        var info = Assert.Single(_started);
        Assert.Equal(AdminPath, info.FileName);
        Assert.True(info.UseShellExecute); // through the shell, so Windows asks for elevation
    }

    [Fact]
    public void Each_poll_shows_one_toast_and_the_first_registers_for_clicks()
    {
        NotificationDto[] first = [TestData.Request(1)];
        NotificationDto[] second = [TestData.Tamper(2, "policy_repaired"), TestData.Request(3)];

        _toasts.Show(first);
        _toasts.Show(second);

        Assert.Equal(1, _windows.Listens);
        Assert.True(_toasts.Listening);
        Assert.Equal([NotificationToasts.For(first).GetContent(), NotificationToasts.For(second).GetContent()], _windows.Shown.Select(t => t.GetContent()));
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void Nothing_is_registered_until_the_service_answers_the_user_as_a_parent()
    {
        Assert.Equal(0, _windows.Listens);
        Assert.False(_toasts.Listening);

        _toasts.Admitted();
        _toasts.Admitted();

        Assert.Equal(1, _windows.Listens);
        Assert.True(_toasts.Listening);
    }

    [Theory]
    [InlineData("toast")]
    [InlineData("button")]
    public void A_click_on_the_toast_or_its_open_button_opens_family_management_on_the_tray_thread(string where)
    {
        _toasts.Show([TestData.Tamper(1, "network_repaired")]);
        var toast = ToastXml.Of(_windows.Shown.Single());
        var arguments = where == "toast"
            ? (string)toast.Attribute("launch")!
            : ToastXml.Buttons(toast).Single(b => b.Activation is null).Arguments!;

        _windows.Click(arguments);

        // Not on Windows' thread: it gets its answer first, and the UAC prompt may wait for the parent.
        Assert.Empty(_started);
        RunPosted();
        OpenedTheAdminAppOnce();
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void Clicks_on_toasts_from_earlier_runs_reach_the_notifier_once_it_is_admitted()
    {
        _toasts.Admitted();

        _windows.Click("action=open");
        RunPosted();

        OpenedTheAdminAppOnce();
        Assert.Empty(_windows.Shown);
    }

    [Fact]
    public void A_click_it_does_not_know_opens_nothing_and_is_logged()
    {
        _toasts.Admitted();

        _windows.Click("dismiss");
        RunPosted();

        Assert.Empty(_started);
        Assert.Contains("'dismiss'", Assert.Single(_log.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void Started_by_a_click_it_registers_at_once_and_the_click_opens_family_management_once()
    {
        _toasts.StartedByClick();

        // The click is on its way through COM, not yet here: nothing opens by itself.
        Assert.Equal(1, _windows.Listens);
        Assert.Empty(_started);

        _windows.Click("action=open");
        RunPosted();
        _toasts.Admitted();
        _toasts.Show([TestData.Request(1)]);

        OpenedTheAdminAppOnce();
        Assert.Equal(1, _windows.Listens);
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void Started_by_a_click_Windows_cannot_deliver_it_opens_family_management_itself()
    {
        _windows.ListenFailure = new InvalidOperationException("Failed to register notification activator");

        _toasts.StartedByClick();

        OpenedTheAdminAppOnce();
        Assert.False(_toasts.Listening);
        Assert.Equal("Windows did not register the notifier for toast clicks: Failed to register notification activator", Assert.Single(_log.Lines));
    }

    [Fact]
    public void A_refused_registration_is_logged_once_and_toasts_still_show()
    {
        _windows.ListenFailure = new InvalidOperationException("Failed initializing notifications");

        _toasts.Admitted();
        _toasts.Show([TestData.Request(1)]);
        _toasts.Show([TestData.Request(2)]);

        Assert.Equal(1, _windows.Listens);
        Assert.Equal(2, _windows.Shown.Count);
        Assert.Single(_log.Lines);
    }

    [Fact]
    public void A_toast_Windows_refuses_is_logged_and_the_next_one_still_comes()
    {
        _windows.ShowFailure = new COMException("Element not found.", unchecked((int)0x80070490));

        _toasts.Show([TestData.Request(1)]);
        _windows.ShowFailure = new InvalidOperationException("Failed initializing notifications");
        _toasts.Show([TestData.Request(2)]);
        _windows.ShowFailure = null;
        _toasts.Show([TestData.Request(3)]);

        Assert.Equal(["A toast could not be shown: Element not found.", "A toast could not be shown: Failed initializing notifications"], _log.Lines);
        Assert.Single(_windows.Shown);
    }

    [Fact]
    public void A_click_that_cannot_start_the_admin_app_is_logged()
    {
        var log = new ListLog();
        var toasts = new ParentToasts(_windows, new AdminLauncher("admin.exe", _ => throw new FileNotFoundException("missing"), log), log, action => action());
        toasts.Admitted();

        _windows.Click("action=open");

        Assert.StartsWith("The admin app admin.exe could not be started", Assert.Single(log.Lines), StringComparison.Ordinal);
    }

    /// <summary>
    /// The developer's PC rule (CLAUDE.md): only <c>WindowsToasts</c> may use the toolkit class that registers the app in
    /// HKCU, and no test may reach it — nor the tray, which would use it and show an icon.
    /// </summary>
    [Fact]
    public void Only_WindowsToasts_uses_the_registering_toolkit_class_and_no_test_does()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "WebFilter.slnx"))) root = root.Parent ?? throw new InvalidOperationException("WebFilter.slnx not found");

        Assert.Equal(["ToastPlatform.cs"], Using(Path.Combine(root.FullName, "src", "WebFilter.Notifier"), Registering()));
        Assert.Empty(Using(Path.Combine(root.FullName, "tests", "WebFilter.Notifier.Tests"), RealToasts()));
    }

    /// <summary>The .cs files (not obj/bin) whose code, outside comments, matches <paramref name="pattern"/>.</summary>
    private static string[] Using(string folder, Regex pattern) =>
        [.. Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadLines(f).Any(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal) && pattern.IsMatch(line)))
            .Select(f => Path.GetFileName(f)!)
            .Order()];

    /// <summary>The toolkit class that registers on first use, and the builder's Show, which goes through it.</summary>
    [GeneratedRegex(@"ToastNotificationManagerCompat\.|\)\s*\.Show\(\s*\)")]
    private static partial Regex Registering();

    [GeneratedRegex(@"ToastNotificationManagerCompat\.|new WindowsToasts\(|new TrayContext\(|\)\s*\.Show\(\s*\)")]
    private static partial Regex RealToasts();
}

using WebFilter.Notifier.Resources;

namespace WebFilter.Notifier;

/// <summary>
/// The tray icon — "open family management" (a double click, or the menu) and "exit" — and the toasts for what each poll
/// brings (<see cref="ParentToasts"/>). No logic of its own: polling, wording and toasts live in <see cref="NotifierLoop"/>,
/// <see cref="NotificationTexts"/>, <see cref="NotificationToasts"/> and <see cref="ParentToasts"/>.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly AdminLauncher _admin;
    private readonly ParentToasts _toasts;
    private readonly INotifierLog _log;
    private readonly CancellationTokenSource _stop = new();

    /// <param name="startedByClick">Windows started the notifier for a click on a toast, which it takes first.</param>
    public TrayContext(NotifierLoop loop, AdminLauncher admin, IToastPlatform windows, bool startedByClick, INotifierLog log)
    {
        _admin = admin;
        _log = log;
        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
        menu.Items.Add(Strings.Menu_Open, null, (_, _) => _admin.Open());
        menu.Items.Add(Strings.Menu_Exit, null, (_, _) => ExitThread());
        _icon = new NotifyIcon { Icon = LoadIcon(), Text = Strings.Tray_Text, ContextMenuStrip = menu, Visible = true };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _admin.Open();
        };

        // WinForms' context for this thread, installed with the menu above: a toast click opens the admin app here.
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _toasts = new ParentToasts(windows, admin, log, action => ui.Post(_ => action(), null));
        if (startedByClick) _toasts.StartedByClick();
        _ = RunAsync(loop);
    }

    private async Task RunAsync(NotifierLoop loop)
    {
        try
        {
            // Returns when the service says this user is not an administrator, or on exit.
            if (await loop.RunAsync(_toasts.Show, _toasts.Admitted, _stop.Token) == NotifierExit.Stopped) return;
        }
        catch (Exception ex)
        {
            _log.Write($"The notifier stopped: {ex}");
        }
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _stop.Cancel();
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _stop.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The shield at the tray's size, from the icon embedded next to the exe's own.</summary>
    private static Icon LoadIcon()
    {
        using var stream = typeof(TrayContext).Assembly.GetManifestResourceStream("WebFilter.Notifier.notifier.ico");
        return stream is null ? SystemIcons.Information : new Icon(stream, SystemInformation.SmallIconSize);
    }
}

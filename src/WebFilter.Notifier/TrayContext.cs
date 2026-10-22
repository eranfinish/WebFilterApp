using WebFilter.Contracts.Notify;
using WebFilter.Notifier.Resources;

namespace WebFilter.Notifier;

/// <summary>
/// The tray icon: a balloon for what each poll brings, "open family management" (the balloon, a double click, or the menu)
/// and "exit". No logic of its own: polling and wording live in <see cref="NotifierLoop"/> and <see cref="NotificationTexts"/>.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly TimeSpan BalloonTime = TimeSpan.FromSeconds(10);

    private readonly NotifyIcon _icon;
    private readonly AdminLauncher _admin;
    private readonly INotifierLog _log;
    private readonly CancellationTokenSource _stop = new();

    public TrayContext(NotifierLoop loop, AdminLauncher admin, INotifierLog log)
    {
        _admin = admin;
        _log = log;
        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes };
        menu.Items.Add(Strings.Menu_Open, null, (_, _) => _admin.Open());
        menu.Items.Add(Strings.Menu_Exit, null, (_, _) => ExitThread());
        _icon = new NotifyIcon { Icon = LoadIcon(), Text = Strings.Tray_Text, ContextMenuStrip = menu, Visible = true };
        _icon.BalloonTipClicked += (_, _) => _admin.Open();
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _admin.Open();
        };
        _ = RunAsync(loop);
    }

    private async Task RunAsync(NotifierLoop loop)
    {
        try
        {
            // Returns when the service says this user is not an administrator, or on exit.
            if (await loop.RunAsync(Show, _stop.Token) == NotifierExit.Stopped) return;
        }
        catch (Exception ex)
        {
            _log.Write($"The notifier stopped: {ex}");
        }
        ExitThread();
    }

    /// <summary>A balloon that cannot be shown is logged; the next poll's still comes.</summary>
    private void Show(IReadOnlyList<NotificationDto> items)
    {
        try
        {
            var balloon = NotificationTexts.For(items);
            _icon.ShowBalloonTip((int)BalloonTime.TotalMilliseconds, balloon.Title, balloon.Text, balloon.IsWarning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or ObjectDisposedException)
        {
            _log.Write($"A balloon could not be shown: {ex.Message}");
        }
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

using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier;

/// <summary>
/// The parents' toasts (PROTECTION-SPEC §10): shows what each poll brings and turns a click into the admin app. It
/// registers for clicks only once they matter — the service has answered this user as a parent (<see cref="Admitted"/>),
/// Windows started the notifier for a click (<see cref="StartedByClick"/>), or a toast is about to be shown — so a child's
/// notifier, which exits at the service's first answer, registers nothing with Windows. What Windows refuses is logged.
/// Called on the tray's thread; only the clicks come on Windows' own, and <paramref name="post"/> brings them back.
/// </summary>
public sealed class ParentToasts(IToastPlatform windows, AdminLauncher admin, INotifierLog log, Action<Action> post)
{
    private bool _tried;
    private bool _listening;

    /// <summary>Whether clicks reach this notifier; otherwise Windows starts another one for them.</summary>
    public bool Listening => _listening;

    /// <summary>The service answered this user as an administrator: clicks on older toasts come here from now on.</summary>
    public void Admitted() => Listen();

    /// <summary>
    /// Windows started this notifier for a click on a toast (<c>-ToastActivated</c>): the click itself arrives once the
    /// notifier registers for clicks. If Windows refuses that, the click would be lost, so the admin app opens at once.
    /// </summary>
    public void StartedByClick()
    {
        if (!Listen()) admin.Open();
    }

    /// <summary>One toast for what one poll brought; one that cannot be shown is logged, and the next poll's still comes.</summary>
    public void Show(IReadOnlyList<NotificationDto> items)
    {
        Listen();
        try
        {
            windows.Show(NotificationToasts.For(items));
        }
        catch (Exception ex)
        {
            // Whatever Windows answers. Notifications turned off for the app are not an error: Windows keeps the toast quietly.
            log.Write($"A toast could not be shown: {ex.Message}");
        }
    }

    /// <summary>Registers for clicks the first time it is called; a refusal is logged once (the toolkit would refuse again).</summary>
    private bool Listen()
    {
        if (_tried) return _listening;
        _tried = true;
        try
        {
            windows.Listen(Clicked);
            _listening = true;
        }
        catch (Exception ex)
        {
            log.Write($"Windows did not register the notifier for toast clicks: {ex.Message}");
        }
        return _listening;
    }

    /// <summary>
    /// A click, on Windows' thread: the admin app opens on the tray's thread — on the page the toast names, if any — after
    /// Windows has had its answer (the UAC prompt may wait for the parent). The close button never comes here: Windows
    /// dismisses the toast itself.
    /// </summary>
    private void Clicked(string arguments)
    {
        if (NotificationToasts.Opens(arguments))
        {
            var page = NotificationToasts.PageOf(arguments);
            post(() => admin.Open(page));
        }
        else
        {
            log.Write($"A toast click with arguments this notifier does not know was ignored: '{arguments}'.");
        }
    }
}

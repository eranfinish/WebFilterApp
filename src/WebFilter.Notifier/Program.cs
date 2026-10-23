using System.Diagnostics;
using System.Globalization;

namespace WebFilter.Notifier;

/// <summary>
/// <c>WebFilter.Notifier.exe [--pipe &lt;name&gt;] [--unregister]</c> (<see cref="NotifierArguments"/>): composition root
/// only. One per session; Hebrew whatever the Windows display language, like the admin app; nothing it meets at run time
/// is shown as a crash. Windows also starts it for a click on one of its toasts (<c>-ToastActivated</c>): with no notifier
/// in the session it opens the admin app and stays as the session's notifier; next to one, it opens the admin app and exits.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var arguments = NotifierArguments.Parse(args);
        var log = new FileNotifierLog(NotifierPaths.Log);
        // First, before any window or toast: the app id the toasts are registered under.
        var windows = new WindowsToasts(log);
        var admin = new AdminLauncher(AdminLauncher.DefaultPath, Process.Start, log);

        using var instance = arguments.Unregister ? null : SingleInstance.TryAcquire();
        switch (arguments.Start(instance is not null))
        {
            case NotifierStart.Unregister:
                windows.Unregister();
                return 0;
            case NotifierStart.OpenAdmin:
                admin.Open();
                return 0;
            case NotifierStart.Exit:
                return 0;
        }

        var hebrew = CultureInfo.GetCultureInfo("he-IL");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = hebrew;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = hebrew;

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => log.Write($"Unexpected error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write($"Unexpected error: {e.ExceptionObject}");
        ApplicationConfiguration.Initialize();

        var poller = new NotificationPoller(new NotifyPipeClient(arguments.Pipe), new FileCursorStore(NotifierPaths.Cursor), TimeProvider.System, log);
        var loop = new NotifierLoop(poller, (wait, ct) => Task.Delay(wait, ct), log);
        using var tray = new TrayContext(loop, admin, windows, arguments.StartedByClick, log);
        Application.Run(tray);
        return 0;
    }
}

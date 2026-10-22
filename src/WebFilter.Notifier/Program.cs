using System.Diagnostics;
using System.Globalization;
using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier;

/// <summary>
/// <c>WebFilter.Notifier.exe [--pipe &lt;name&gt;]</c>: composition root only. One per session; Hebrew whatever the
/// Windows display language, like the admin app; nothing it meets at run time is shown as a crash.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        using var instance = SingleInstance.TryAcquire();
        if (instance is null) return 0;

        var hebrew = CultureInfo.GetCultureInfo("he-IL");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = hebrew;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = hebrew;

        var log = new FileNotifierLog(NotifierPaths.Log);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => log.Write($"Unexpected error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Write($"Unexpected error: {e.ExceptionObject}");
        ApplicationConfiguration.Initialize();

        var poller = new NotificationPoller(new NotifyPipeClient(PipeName(args)), new FileCursorStore(NotifierPaths.Cursor), TimeProvider.System, log);
        var loop = new NotifierLoop(poller, (wait, ct) => Task.Delay(wait, ct), log);
        var admin = new AdminLauncher(AdminLauncher.DefaultPath, Process.Start, log);
        using var tray = new TrayContext(loop, admin, log);
        Application.Run(tray);
        return 0;
    }

    /// <summary>The notify pipe, or another one for a development service (<c>--pipe name</c> or <c>--pipe=name</c>).</summary>
    internal static string PipeName(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase) && args[i].Length > "--pipe=".Length) return args[i]["--pipe=".Length..].Trim();
            if (args[i].Equals("--pipe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1])) return args[i + 1].Trim();
        }
        return NotifyProtocol.PipeName;
    }
}

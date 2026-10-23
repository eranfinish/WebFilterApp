using WebFilter.Contracts.Notify;

namespace WebFilter.Notifier;

/// <summary>What one start of the notifier does (<see cref="NotifierArguments.Start"/>).</summary>
public enum NotifierStart
{
    /// <summary>The session's notifier: the tray icon, polling and toasts. Started for a click, it takes that click first.</summary>
    Tray,

    /// <summary>
    /// Started by Windows for a click while the session's notifier runs but has not registered for clicks yet (it does
    /// when the service first answers it): this copy opens the admin app and exits.
    /// </summary>
    OpenAdmin,

    /// <summary>The session already has its notifier.</summary>
    Exit,

    /// <summary><c>--unregister</c>: removes this user's toasts and their registration with Windows, then exits.</summary>
    Unregister,
}

/// <summary>
/// <c>WebFilter.Notifier.exe [--pipe &lt;name&gt;] [--unregister]</c>, plus <c>-ToastActivated</c>, which Windows adds when
/// it starts the notifier for a click on one of its toasts (the COM server registered by <see cref="WindowsToasts"/>;
/// COM's own <c>-Embedding</c> may follow and is ignored, like anything else unknown).
/// </summary>
public sealed record NotifierArguments(string Pipe, bool StartedByClick, bool Unregister)
{
    /// <summary>The toolkit's flag on the COM server's command line (ToastNotificationManagerCompat's TOAST_ACTIVATED_LAUNCH_ARG).</summary>
    public const string ToastActivated = "-ToastActivated";

    public const string UnregisterFlag = "--unregister";

    public static NotifierArguments Parse(IReadOnlyList<string> args) =>
        new(PipeName(args), Has(args, ToastActivated), Has(args, UnregisterFlag));

    /// <summary>
    /// What to do, given whether this process holds the session's single-instance mutex (<see cref="SingleInstance"/>,
    /// not taken for <c>--unregister</c>). A click that Windows sent to a new process is never dropped: the session's
    /// notifier, if any, did not get it.
    /// </summary>
    public NotifierStart Start(bool firstInSession) =>
        Unregister ? NotifierStart.Unregister
        : firstInSession ? NotifierStart.Tray
        : StartedByClick ? NotifierStart.OpenAdmin
        : NotifierStart.Exit;

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

    private static bool Has(IReadOnlyList<string> args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
}

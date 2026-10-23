using System.Runtime.InteropServices;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.UI.Notifications;

namespace WebFilter.Notifier;

/// <summary>
/// Windows' toast notifications as the notifier uses them. The real one, <see cref="WindowsToasts"/>, registers the
/// notifier with Windows in the current user's registry on first use; tests use a fake and never touch it.
/// </summary>
public interface IToastPlatform
{
    /// <summary>
    /// From now on a click on one of the notifier's toasts — also one an earlier run left in the notification center —
    /// calls <paramref name="clicked"/> with the arguments of the toast or of its button, on a thread of Windows' choosing.
    /// </summary>
    /// <exception cref="InvalidOperationException">Windows did not take the registration.</exception>
    void Listen(Action<string> clicked);

    /// <summary>Shows the toast; it then stays in the notification center. Throws whatever Windows answers (COM, WinRT).</summary>
    void Show(ToastContent toast);

    /// <summary>Removes this user's toasts and registration (<c>--unregister</c>). Best effort; never throws.</summary>
    void Unregister();
}

/// <summary>
/// <see cref="IToastPlatform"/> through the Windows Community Toolkit's ToastNotificationManagerCompat
/// (Microsoft.Toolkit.Uwp.Notifications, MIT) for an app without package identity. Its first use in a process (the
/// class's static constructor) writes, in the current user's HKCU only:
/// <list type="bullet">
/// <item><c>Software\Classes\AppUserModelId\WebFilter.Notifier</c>: <c>DisplayName</c> (the assembly title, "WebFilter"),
/// <c>IconUri</c> (the exe's icon, the shield, saved as
/// <c>%LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\WebFilter.Notifier\Icon.png</c>) and <c>CustomActivator</c>, a
/// CLSID derived from the app id;</item>
/// <item><c>Software\Classes\CLSID\{that CLSID}\LocalServer32</c> = <c>"…\WebFilter.Notifier.exe" -ToastActivated</c>;</item>
/// </list>
/// and registers that COM class in the process (CoRegisterClassObject, on the thread that first uses it — the tray's).
/// So Windows keeps the toasts in the notification center under our name and icon, and a click goes through COM to the
/// notifier that registered; when none has, COM starts the exe with <c>-ToastActivated</c> (and its own
/// <c>-Embedding</c>) and hands the click over once that one registers (<see cref="NotifierArguments"/>). Elevated —
/// only with UAC off, as the Run value starts it as the user — the toolkit also writes HKLM\SOFTWARE\Classes\CLSID and
/// AppID entries for the same CLSID. The keys stay after an uninstall unless <see cref="Unregister"/> ran for the user;
/// they are harmless (they only name the exe). Never used by tests: it would write the developer's HKCU.
/// </summary>
internal sealed class WindowsToasts : IToastPlatform
{
    /// <summary>
    /// The app id (AUMID), set on the process before the toolkit asks the shell for it. Without one the shell derives it
    /// from the install path ("{ProgramFiles GUID}/WebFilter/WebFilter.Notifier.exe"); a fixed one keeps the registry keys
    /// known and the same wherever the app is installed.
    /// </summary>
    public const string AppId = "WebFilter.Notifier";

    /// <summary>Call before any UI and before the toolkit is used, as Windows asks of an explicit app id.</summary>
    public WindowsToasts(INotifierLog log)
    {
        var result = SetCurrentProcessExplicitAppUserModelID(AppId);
        if (result != 0) log.Write($"The notifier's app id could not be set (0x{result:X8}); Windows names its toasts after the install path.");
    }

    public void Listen(Action<string> clicked)
    {
        ToastNotificationManagerCompat.OnActivated += e => clicked(e.Argument);
        // The toolkit keeps a failed start-up or registration to itself; reading its History throws it.
        _ = ToastNotificationManagerCompat.History;
    }

    public void Show(ToastContent toast) =>
        ToastNotificationManagerCompat.CreateToastNotifier().Show(new ToastNotification(toast.GetXml()));

    /// <summary>
    /// The toolkit's own clean-up, for this user: clears the toasts, deletes both keys and the icon folder (it registers
    /// first, in the same call, then removes everything). Only the user who runs it.
    /// </summary>
    public void Unregister() => ToastNotificationManagerCompat.Uninstall();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

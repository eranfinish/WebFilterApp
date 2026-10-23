using System.Globalization;
using Microsoft.Toolkit.Uwp.Notifications;
using WebFilter.Contracts.Notify;
using WebFilter.Notifier.Resources;

namespace WebFilter.Notifier;

/// <summary>
/// The toast for what one poll brought (PROTECTION-SPEC §10). Title and text come from <see cref="NotificationTexts"/>;
/// a click on the toast or on its "open family management" button carries <c>action=open</c>, which opens the admin app
/// (<see cref="ParentToasts"/>), and for new Windows accounts alone also <c>page=devices</c>, which opens it on the
/// devices page with the accounts' cards. Tamper — alone or in a summary — uses the reminder scenario, so the toast stays
/// on the screen until the parent clicks it or "close" (Windows wants at least one button for that; "close" is a system
/// dismissal that never reaches the notifier). Requests, silent computers and new accounts show for the normal few
/// seconds. All stay in the notification center (Win+N) until opened or cleared. Data only: <see cref="IToastPlatform"/>
/// shows it.
/// </summary>
public static class NotificationToasts
{
    /// <summary>The activation argument a click carries (toast body and open button alike): <c>action=open</c>.</summary>
    public const string ActionKey = "action", OpenAction = "open";

    /// <summary>
    /// Next to <c>action=open</c>, the admin app's page to open (<c>page=devices</c>, <see cref="AdminLauncher.Open"/>);
    /// without it the admin app opens on its dashboard. Toasts of older notifiers carry none.
    /// </summary>
    public const string PageKey = "page";

    public static ToastContent For(IReadOnlyList<NotificationDto> items, TimeZoneInfo? zone = null)
    {
        var text = NotificationTexts.For(items, zone);
        var open = new ToastButton().SetContent(Strings.Toast_Open).AddArgument(ActionKey, OpenAction);
        var builder = new ToastContentBuilder().AddArgument(ActionKey, OpenAction);
        if (PageFor(items) is { } page)
        {
            builder.AddArgument(PageKey, page);
            open.AddArgument(PageKey, page);
        }
        builder.AddText(text.Title).AddText(text.Text).AddButton(open);
        if (items.Any(i => i.Kind == NotificationKinds.Tamper))
            builder.SetToastScenario(ToastScenario.Reminder).AddButton(new ToastButtonDismiss(Strings.Toast_Close));

        // The texts' language (Hebrew, pinned at start), not the Windows display language the toast would assume.
        var content = builder.GetToastContent();
        content.Visual.Language = (Strings.Culture ?? CultureInfo.CurrentUICulture).Name;
        return content;
    }

    /// <summary>Whether a click's arguments (the toast's or a button's) ask for the admin app. Parsing never throws.</summary>
    public static bool Opens(string? arguments) =>
        ToastArguments.Parse(arguments ?? "").TryGetValue(ActionKey, out var action) && action == OpenAction;

    /// <summary>The admin app's page a click's arguments name, or null for its start page. Parsing never throws.</summary>
    public static string? PageOf(string? arguments) =>
        ToastArguments.Parse(arguments ?? "").TryGetValue(PageKey, out var page) && !string.IsNullOrEmpty(page) ? page : null;

    /// <summary>
    /// The devices page when the poll brought only new Windows accounts. Anything else opens the dashboard, which lists the
    /// pending requests and the new accounts' cards as well.
    /// </summary>
    private static string? PageFor(IReadOnlyList<NotificationDto> items) =>
        items.All(i => i.Kind == NotificationKinds.Account) ? AdminLauncher.DevicesPage : null;
}

using System.Globalization;
using Microsoft.Toolkit.Uwp.Notifications;
using WebFilter.Contracts.Notify;
using WebFilter.Notifier.Resources;

namespace WebFilter.Notifier;

/// <summary>
/// The toast for what one poll brought (PROTECTION-SPEC §10). Title and text come from <see cref="NotificationTexts"/>;
/// a click on the toast or on its "open family management" button carries <c>action=open</c>, which opens the admin app
/// (<see cref="ParentToasts"/>). Tamper — alone or in a summary — uses the reminder scenario, so the toast stays on the
/// screen until the parent clicks it or "close" (Windows wants at least one button for that; "close" is a system
/// dismissal that never reaches the notifier). Requests and silent computers show for the normal few seconds. All stay in
/// the notification center (Win+N) until opened or cleared. Data only: <see cref="IToastPlatform"/> shows it.
/// </summary>
public static class NotificationToasts
{
    /// <summary>The activation argument a click carries (toast body and open button alike): <c>action=open</c>.</summary>
    public const string ActionKey = "action", OpenAction = "open";

    public static ToastContent For(IReadOnlyList<NotificationDto> items, TimeZoneInfo? zone = null)
    {
        var text = NotificationTexts.For(items, zone);
        var builder = new ToastContentBuilder()
            .AddArgument(ActionKey, OpenAction)
            .AddText(text.Title)
            .AddText(text.Text)
            .AddButton(new ToastButton().SetContent(Strings.Toast_Open).AddArgument(ActionKey, OpenAction));
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
}

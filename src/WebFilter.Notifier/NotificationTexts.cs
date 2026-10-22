using System.Globalization;
using WebFilter.Contracts.Notify;
using WebFilter.Notifier.Resources;

namespace WebFilter.Notifier;

/// <summary>A balloon: its title, text and whether it warns (tamper) rather than informs.</summary>
public sealed record Balloon(string Title, string Text, bool IsWarning);

/// <summary>
/// The Hebrew wording of the balloons (PROTECTION-SPEC §10). Notifications carry member ids, not names, and the names are
/// in the settings only the elevated admin pipe serves; the notifier never elevates, so a balloon names the computer (a
/// readable device id such as the computer's name) and the request's target, never the child.
/// </summary>
public static class NotificationTexts
{
    /// <summary>Windows cuts balloon titles at 63 characters and texts at 255.</summary>
    public const int MaxTitle = 63, MaxText = 255;

    private const char Lrm = '‎';

    /// <summary>One balloon for what one poll brought: the notification itself, or a summary of several.</summary>
    public static Balloon For(IReadOnlyList<NotificationDto> items, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("No notifications to show.", nameof(items));
        var balloon = items.Count == 1 ? One(items[0], zone ?? TimeZoneInfo.Local) : Summary(items);
        return balloon with { Title = Cut(balloon.Title, MaxTitle), Text = Cut(balloon.Text, MaxText) };
    }

    private static Balloon One(NotificationDto item, TimeZoneInfo zone) => item.Kind switch
    {
        NotificationKinds.Request => new(Strings.Request_Title, Join(Request(item), Strings.Open_Hint), false),
        NotificationKinds.Tamper => Tamper(item),
        NotificationKinds.Device => new(Strings.Device_Title, Join(Device(item, zone), Strings.Open_Hint), true),
        _ => new(Strings.Other_Title, Strings.Other_Text, false),
    };

    /// <summary>"A request to open games.example (from kids-pc)."; detail is "kind:target", split at the first ':'; url targets are hosts.</summary>
    private static string Request(NotificationDto item)
    {
        var detail = item.Detail ?? "";
        var colon = detail.IndexOf(':', StringComparison.Ordinal);
        var (kind, target) = colon < 0 ? (detail, "") : (detail[..colon], detail[(colon + 1)..]);
        var text = (kind, target.Length > 0) switch
        {
            ("url", true) => Format(Strings.Request_Url, Ltr(target)),
            ("youtube", _) => Strings.Request_YouTube,
            ("app", true) => Format(Strings.Request_App, Ltr(FileName(target))),
            ("time", true) when int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) => Format(Strings.Request_Time, minutes),
            _ => Strings.Request_Other,
        };
        return item.DeviceId is { Length: > 0 } device ? Format(Strings.Request_FromComputer, text, Ltr(device)) : Format(Strings.Request_Sentence, text);
    }

    private static Balloon Tamper(NotificationDto item)
    {
        var computer = Computer(item.DeviceId);
        var (title, text) = item.Detail switch
        {
            "policy_repaired" => (Strings.Tamper_Policy_Title, Format(Strings.Tamper_Policy_Text, computer)),
            "network_repaired" => (Strings.Tamper_Network_Title, Format(Strings.Tamper_Network_Text, computer)),
            // Not an accusation: a power cut or a forced shutdown looks the same.
            "unclean_stop" => (Strings.Tamper_UncleanStop_Title, Format(Strings.Tamper_UncleanStop_Text, computer)),
            _ => (Strings.Tamper_Other_Title, Format(Strings.Tamper_Other_Text, computer)),
        };
        return new(title, Join(text, Strings.Open_Hint), true);
    }

    /// <summary>"kids-pc has not reported for over a day (last on 21.10 at 18:30)."; detail is when it last reported (ISO-8601), if ever.</summary>
    private static string Device(NotificationDto item, TimeZoneInfo zone)
    {
        var computer = Computer(item.DeviceId);
        return DateTimeOffset.TryParse(item.Detail, CultureInfo.InvariantCulture, DateTimeStyles.None, out var last)
            ? Format(Strings.Device_Text, computer, TimeZoneInfo.ConvertTime(last, zone).ToString("dd.MM HH:mm", CultureInfo.InvariantCulture))
            : Format(Strings.Device_NeverText, computer);
    }

    /// <summary>"3 new notifications": one line per kind, with how many.</summary>
    private static Balloon Summary(IReadOnlyList<NotificationDto> items)
    {
        int Count(string kind) => items.Count(i => i.Kind == kind);
        var (requests, tamper, devices) = (Count(NotificationKinds.Request), Count(NotificationKinds.Tamper), Count(NotificationKinds.Device));
        var lines = new[]
        {
            requests == 0 ? null : requests == 1 ? Strings.Summary_Request : Format(Strings.Summary_Requests, requests),
            tamper == 0 ? null : tamper == 1 ? Strings.Summary_Tamper : Format(Strings.Summary_Tampers, tamper),
            devices == 0 ? null : devices == 1 ? Strings.Summary_Device : Format(Strings.Summary_Devices, devices),
            Strings.Open_Hint,
        };
        return new(Format(Strings.Summary_Title, items.Count), string.Join("\n", lines.Where(l => l is not null)), tamper + devices > 0);
    }

    private static string Computer(string? deviceId) => deviceId is { Length: > 0 } ? Ltr(deviceId) : Strings.Computer_Unknown;

    private static string FileName(string path) => System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } file ? file : path;

    /// <summary>A left-to-right value (host, file, computer name) kept in one piece inside a Hebrew sentence.</summary>
    private static string Ltr(string value) => $"{Lrm}{value}{Lrm}";

    private static string Join(string first, string second) => $"{first} {second}";

    private static string Format(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}

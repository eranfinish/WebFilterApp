using System.Globalization;
using WebFilter.Admin.Resources;
using WebFilter.Admin.Services;

namespace WebFilter.Admin.ViewModels;

/// <summary>A choice in a combo box: the wire value and its user-facing label.</summary>
public sealed record Option(string Value, string Label);

/// <summary>User-facing wording built from <see cref="Strings"/>.</summary>
internal static class Texts
{
    public static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);

    public static string Minutes(int minutes) => minutes == 1 ? Strings.Minutes_One : Format(Strings.Minutes_Many, minutes);

    /// <summary>
    /// Text for a wire code from a closed list (reason codes, event types, roles, …), stored as "<paramref name="prefix"/><paramref name="code"/>"
    /// in Strings.resx, with a '-' in the code written '_' (resource names are C# identifiers: "school-tools" is
    /// Category_school_tools). An unknown code is shown as is rather than hidden.
    /// </summary>
    public static string Code(string prefix, string? code) =>
        string.IsNullOrEmpty(code) ? "" : Strings.ResourceManager.GetString(prefix + code.Replace('-', '_'), Strings.Culture) ?? code;

    public static IReadOnlyList<Option> Options(string prefix, params string[] codes) => [.. codes.Select(c => new Option(c, Code(prefix, c)))];

    public static string ConnectionState(AdminConnectionState state) => state switch
    {
        AdminConnectionState.Connecting => Strings.Connection_Connecting,
        AdminConnectionState.Connected => Strings.Connection_Connected,
        AdminConnectionState.ServiceNotRunning => Strings.Connection_ServiceNotRunning,
        AdminConnectionState.NotElevated => Strings.Connection_NotElevated,
        AdminConnectionState.ConnectionLost => Strings.Connection_Lost,
        _ => Strings.Connection_NotConnected,
    };

    /// <summary>
    /// The page-level message for a failed request. Connection problems return null: the window-wide banner already
    /// explains them, and a second message on the page would only repeat it.
    /// </summary>
    public static string? Error(AdminClientException error) => error switch
    {
        AdminConnectionException { Error: AdminConnectionError.Timeout } => Strings.Error_Timeout,
        AdminConnectionException => null,
        AdminServerException { Code: "unsupported" } => Strings.Error_Unsupported,
        AdminServerException { Code: FamilyContext.NotHub } => Strings.Error_NotHub,
        AdminServerException { Code: Contracts.Admin.ReplaceSettings.ConflictCode } => Strings.Error_Conflict,
        AdminServerException { Code: AdminServerException.UnexpectedReply } => Strings.Error_UnexpectedReply,
        _ => Format(Strings.Error_Rejected, error.Message),
    };

    /// <summary>Short form for the status box under the navigation.</summary>
    public static string ConnectionShort(AdminConnectionState state) => state switch
    {
        AdminConnectionState.Connecting => Strings.ConnectionShort_Connecting,
        AdminConnectionState.Connected => Strings.ConnectionShort_Connected,
        AdminConnectionState.ServiceNotRunning => Strings.ConnectionShort_ServiceNotRunning,
        AdminConnectionState.NotElevated => Strings.ConnectionShort_NotElevated,
        AdminConnectionState.ConnectionLost => Strings.ConnectionShort_Lost,
        _ => Strings.ConnectionShort_NotConnected,
    };

    /// <summary>Left-to-right mark (U+200E).</summary>
    public const char Lrm = '‎';

    /// <summary>
    /// Keeps a left-to-right value (site pattern, URL, path) in one piece inside a Hebrew sentence: without the marks
    /// the Unicode bidi rules move leading "*." or a trailing "/" to the other end ("example.com.*").
    /// </summary>
    public static string? Ltr(string? value) => string.IsNullOrEmpty(value) ? value : $"{Lrm}{value}{Lrm}";

    /// <summary>
    /// A name that may be Hebrew or Latin, in its own direction: a Latin one (by its first letter) gets the left-to-right
    /// marks of <see cref="Ltr"/>, so "Khan Kids (sim)" does not become "(Khan Kids (sim".
    /// </summary>
    public static string Natural(string value)
    {
        foreach (var c in value)
        {
            if (c is >= '\u0590' and <= '\u08FF') return value;
            if (char.IsLetter(c)) return Ltr(value)!;
        }
        return value;
    }

    public static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    public static string Join(string separator, params string?[] parts) => string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}

using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace WebFilter.Notifier.Resources;

/// <summary>
/// The notifier's texts: Strings.resx (Hebrew, the neutral language) and Strings.en.resx. Each property reads the entry of
/// its own name; tests check that every property has an entry in both files.
/// </summary>
internal static class Strings
{
    private static readonly ResourceManager Manager = new("WebFilter.Notifier.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>Null: the current UI culture (pinned to Hebrew at start, like the admin app).</summary>
    public static CultureInfo? Culture { get; set; }

    public static string Tray_Text => Get();
    public static string Menu_Open => Get();
    public static string Menu_Exit => Get();
    public static string Open_Hint => Get();

    public static string Request_Title => Get();
    public static string Request_Url => Get();
    public static string Request_YouTube => Get();
    public static string Request_App => Get();
    public static string Request_Time => Get();
    public static string Request_Other => Get();
    public static string Request_Sentence => Get();
    public static string Request_FromComputer => Get();

    public static string Tamper_Policy_Title => Get();
    public static string Tamper_Policy_Text => Get();
    public static string Tamper_Network_Title => Get();
    public static string Tamper_Network_Text => Get();
    public static string Tamper_UncleanStop_Title => Get();
    public static string Tamper_UncleanStop_Text => Get();
    public static string Tamper_Other_Title => Get();
    public static string Tamper_Other_Text => Get();
    public static string Computer_Unknown => Get();

    public static string Device_Title => Get();
    public static string Device_Text => Get();
    public static string Device_NeverText => Get();

    public static string Summary_Title => Get();
    public static string Summary_Request => Get();
    public static string Summary_Requests => Get();
    public static string Summary_Tamper => Get();
    public static string Summary_Tampers => Get();
    public static string Summary_Device => Get();
    public static string Summary_Devices => Get();

    public static string Other_Title => Get();
    public static string Other_Text => Get();

    /// <summary>A missing entry shows its name rather than failing a balloon (the tests make sure none is missing).</summary>
    private static string Get([CallerMemberName] string name = "") => Manager.GetString(name, Culture) ?? name;
}

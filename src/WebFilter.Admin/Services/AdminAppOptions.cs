using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace WebFilter.Admin.Services;

/// <summary>Command line: <c>WebFilter.Admin.exe [--pipe &lt;name&gt;]</c> (also <c>--pipe=&lt;name&gt;</c>).</summary>
public sealed record AdminAppOptions(string PipeName)
{
    public static AdminAppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var pipe = AdminClientOptions.DefaultPipeName;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--pipe=", StringComparison.OrdinalIgnoreCase))
                pipe = arg["--pipe=".Length..];
            else if (arg.Equals("--pipe", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count)
                pipe = args[++i];
        }
        return new AdminAppOptions(string.IsNullOrWhiteSpace(pipe) ? AdminClientOptions.DefaultPipeName : pipe.Trim());
    }
}

/// <summary>
/// The admin UI is Hebrew whatever the Windows display language (many Israeli PCs run English Windows), so the culture
/// is pinned before any window opens. Strings.en.resx is kept complete for a later language switch.
/// </summary>
public static class UiCulture
{
    public static readonly CultureInfo Hebrew = CultureInfo.GetCultureInfo("he-IL");

    private static bool _languageSet;

    /// <summary>Sets the process culture; the WPF default language can only be set once per process.</summary>
    public static void Apply(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        if (_languageSet) return;
        _languageSet = true;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
    }
}

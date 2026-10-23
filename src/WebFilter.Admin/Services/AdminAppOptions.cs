using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace WebFilter.Admin.Services;

/// <summary>
/// Command line: <c>WebFilter.Admin.exe [--pipe &lt;name&gt;] [--page &lt;page&gt;]</c> (also <c>--pipe=&lt;name&gt;</c> and
/// <c>--page=&lt;page&gt;</c>). The shell keeps the arguments through the UAC prompt, so the elevated app gets them too.
/// </summary>
/// <param name="StartPage">
/// The page to open on, lower case (<see cref="ViewModels.MainViewModel.Start"/>): dashboard, requests, members, rules,
/// activity, devices or settings; null for the dashboard. The parents' notifier passes <c>--page devices</c> for a new
/// Windows account.
/// </param>
public sealed record AdminAppOptions(string PipeName, string? StartPage = null)
{
    public static AdminAppOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var (pipe, page) = (AdminClientOptions.DefaultPipeName, (string?)null);
        for (var i = 0; i < args.Count; i++)
        {
            if (Value(args, ref i, "--pipe") is { } p) pipe = p;
            else if (Value(args, ref i, "--page") is { } g) page = g;
        }
        return new AdminAppOptions(string.IsNullOrWhiteSpace(pipe) ? AdminClientOptions.DefaultPipeName : pipe.Trim(),
            string.IsNullOrWhiteSpace(page) ? null : page.Trim().ToLowerInvariant());
    }

    /// <summary>The value of the option <paramref name="name"/> at <paramref name="i"/> (<c>name value</c> or <c>name=value</c>), or null.</summary>
    private static string? Value(IReadOnlyList<string> args, ref int i, string name)
    {
        var arg = args[i];
        if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return arg[(name.Length + 1)..];
        return arg.Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count ? args[++i] : null;
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

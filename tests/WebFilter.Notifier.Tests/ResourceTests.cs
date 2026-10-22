using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WebFilter.Notifier.Resources;

namespace WebFilter.Notifier.Tests;

/// <summary>Every notifier text exists in Hebrew and English, with the same placeholders.</summary>
public sealed partial class ResourceTests
{
    private static readonly Dictionary<string, string> Hebrew = Load("Strings.resx");
    private static readonly Dictionary<string, string> English = Load("Strings.en.resx");

    private static readonly string[] Properties =
        [.. typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).Order()];

    [Fact]
    public void Hebrew_and_English_have_the_same_keys_and_placeholders()
    {
        Assert.Equal(Hebrew.Keys.Order(), English.Keys.Order());
        foreach (var (key, value) in Hebrew)
            Assert.True(Placeholders(value) == Placeholders(English[key]), $"{key}: placeholders differ");
    }

    [Fact]
    public void Every_text_the_code_reads_is_in_the_files_and_the_compiled_resources()
    {
        Assert.Equal(Hebrew.Keys.Order(), Properties);
        foreach (var name in Properties)
        {
            var value = (string)typeof(Strings).GetProperty(name)!.GetValue(null)!;
            Assert.Equal(Hebrew[name], value);
        }
    }

    [Fact]
    public void The_neutral_texts_are_Hebrew_and_the_English_ones_compile()
    {
        string[] notHebrew = ["Request_Sentence"];
        foreach (var (key, value) in Hebrew.Where(kv => !notHebrew.Contains(kv.Key)))
            Assert.True(HebrewLetter().IsMatch(value), $"{key} has no Hebrew text: {value}");

        // The English satellite is built and found (read directly: Strings.Culture is shared by tests running in parallel).
        var manager = new System.Resources.ResourceManager("WebFilter.Notifier.Resources.Strings", typeof(Strings).Assembly);
        Assert.Equal(English["Menu_Open"], manager.GetString("Menu_Open", CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void The_tray_menu_says_what_the_spec_says()
    {
        Assert.Equal("פתח את ניהול המשפחה", Strings.Menu_Open);
        Assert.Equal("יציאה", Strings.Menu_Exit);
        Assert.True(Strings.Tray_Text.Length < 128, "a NotifyIcon's text is at most 127 characters");
    }

    private static string Placeholders(string text) => string.Join(",", Placeholder().Matches(text).Select(m => m.Value).Order());

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Resources", file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[֐-׿]")]
    private static partial Regex HebrewLetter();
}

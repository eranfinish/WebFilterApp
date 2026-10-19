using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using WebFilter.Admin.Resources;
using WebFilter.Admin.ViewModels;
using WebFilter.Core.Decisions;
using WebFilter.Core.Engine;

namespace WebFilter.Admin.Tests;

/// <summary>Every user-facing text exists in Hebrew and English, and every closed list the service sends has wording.</summary>
public sealed partial class ResourceTests
{
    private static readonly Dictionary<string, string> Hebrew = Load("Strings.resx");
    private static readonly Dictionary<string, string> English = Load("Strings.en.resx");

    [Fact]
    public void Hebrew_and_English_have_the_same_keys_and_placeholders()
    {
        Assert.Equal(Hebrew.Keys.Order(), English.Keys.Order());
        foreach (var (key, value) in Hebrew)
            Assert.True(Placeholders(value) == Placeholders(English[key]), $"{key}: placeholders differ");
    }

    [Fact]
    public void The_generated_Strings_class_matches_the_resx()
    {
        var properties = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string)).Select(p => p.Name).Order();

        Assert.Equal(Hebrew.Keys.Order(), properties);
    }

    [Fact]
    public void The_neutral_strings_are_Hebrew()
    {
        string[] notHebrew = ["Format_Time", "Format_DateTime", "Format_Date", "Common_None", "Settings_Col_YouTubeMode", "DeviceOs_windows", "DeviceOs_android"];
        foreach (var (key, value) in Hebrew.Where(kv => !notHebrew.Contains(kv.Key)))
            Assert.True(HebrewLetter().IsMatch(value), $"{key} has no Hebrew text: {value}");
    }

    [Fact]
    public void Every_reason_code_has_wording()
    {
        foreach (var code in ReasonCodes.AllWire.Where(c => c.Length > 0))
        {
            Assert.True(Hebrew.ContainsKey("Reason_" + code), $"missing Reason_{code}");
            Assert.NotEqual(code, Texts.Code("Reason_", code));
        }
    }

    [Fact]
    public void Every_event_type_has_wording()
    {
        Assert.Equal(Enum.GetValues<EventType>().Length, ActivityViewModel.EventTypes.Length);
        foreach (var type in ActivityViewModel.EventTypes) Assert.True(Hebrew.ContainsKey("EventType_" + type), $"missing EventType_{type}");
    }

    [Theory]
    [InlineData("TimeState_", new[] { "ok", "warn", "expired", "outside_window", "unlimited" })]
    [InlineData("RequestKind_", new[] { "url", "youtube", "app", "time" })]
    [InlineData("RequestStatus_", new[] { "pending", "approved", "denied" })]
    [InlineData("Role_", new[] { "child", "teen", "adult" })]
    [InlineData("FilterMode_", new[] { "unrestricted", "blocklist", "allowlist" })]
    [InlineData("YouTubeMode_", new[] { "off", "restricted", "allowlist", "unrestricted" })]
    [InlineData("RuleType_", new[] { "domain", "wildcard", "prefix", "regex" })]
    [InlineData("AdminChange_", new[] { "add_rule", "remove_rule", "set_member", "extend_time", "pause", "resume", "start_homework", "stop_homework", "approve_request", "deny_request", "replace_settings", "local_pause" })]
    [InlineData("DeviceOs_", new[] { "windows", "android" })]
    public void Every_wire_value_has_wording(string prefix, string[] codes)
    {
        foreach (var code in codes) Assert.True(Hebrew.ContainsKey(prefix + code), $"missing {prefix}{code}");
    }

    [Fact]
    public void The_devices_page_texts_exist_in_both_languages()
    {
        var keys = Hebrew.Keys.Where(k => k.StartsWith("Devices_", StringComparison.Ordinal) || k.StartsWith("Device_", StringComparison.Ordinal)
                                          || k.StartsWith("Pairing_", StringComparison.Ordinal)).ToList();
        string[] expected =
        [
            "Nav_Devices", "Devices_Intro", "Devices_NotHub", "Devices_Pair", "Devices_Revoke", "Devices_RevokeConfirm", "Done_DeviceRevoked",
            "Devices_AlreadyRevoked", "Device_NeverSeen", "Device_SeenMinutesAgo", "Pairing_ValidFor", "Pairing_Expired", "Pairing_Copy", "Pairing_CopyFailed",
        ];

        Assert.True(keys.Count >= 40, $"only {keys.Count} device texts");
        foreach (var key in keys.Concat(expected))
        {
            Assert.False(string.IsNullOrWhiteSpace(Hebrew.GetValueOrDefault(key)), $"{key} missing in Hebrew");
            Assert.False(string.IsNullOrWhiteSpace(English.GetValueOrDefault(key)), $"{key} missing in English");
            Assert.False(string.IsNullOrEmpty(Strings.ResourceManager.GetString(key, Strings.Culture)), $"{key} missing from the compiled resources");
        }
        // The confirmation explains what revoking does and does not do.
        Assert.Contains("ימשיך לאכוף את ההגדרות האחרונות", Hebrew["Devices_RevokeConfirm"]);
        Assert.Contains("keeps enforcing the last settings", English["Devices_RevokeConfirm"]);
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

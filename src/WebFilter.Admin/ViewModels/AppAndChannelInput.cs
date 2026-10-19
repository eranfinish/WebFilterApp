using System.IO;
using System.Text.RegularExpressions;
using WebFilter.Admin.Resources;

namespace WebFilter.Admin.ViewModels;

/// <summary>
/// Checks an approved app before it is sent. The engine matches <c>path</c> against the program's full path and
/// <c>package</c> against a Store app's package family name, both ignoring case (Decider's app match).
/// </summary>
internal static partial class AppRuleValidator
{
    public static readonly string[] Matches = ["path", "package"];

    public static bool TryNormalize(string? input, string match, out string value, out string? error)
    {
        value = "";
        error = null;
        var text = (input ?? "").Trim().Trim('"').Trim();
        if (text.Length == 0)
        {
            error = Strings.Validation_Empty;
            return false;
        }
        switch (match)
        {
            case "path":
                // Every computer has its own user folders and variables, so only a full path means the same program everywhere.
                if (text.Contains('%') || text.IndexOfAny(['*', '?']) >= 0 || text.IndexOfAny(Path.GetInvalidPathChars()) >= 0
                    || !Path.IsPathFullyQualified(text))
                {
                    error = Strings.Validation_AppPathForm;
                    return false;
                }
                if (!text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    error = Strings.Validation_AppPathExe;
                    return false;
                }
                value = Path.GetFullPath(text);
                return true;

            case "package":
                // A full package name (name_version_arch_resource_publisher) is accepted as its family name.
                if (FullName().Match(text) is { Success: true } full) text = $"{full.Groups[1].Value}_{full.Groups[2].Value}";
                if (!FamilyName().IsMatch(text))
                {
                    error = Strings.Validation_AppPackageForm;
                    return false;
                }
                value = text;
                return true;

            default:
                throw new ArgumentException($"Unknown app match '{match}'.", nameof(match));
        }
    }

    [GeneratedRegex(@"^([A-Za-z0-9.\-]{3,50})_[0-9.]+_[A-Za-z0-9]*_[A-Za-z0-9.\-~]*_([0-9a-hjkmnp-tv-z]{13})$", RegexOptions.IgnoreCase)]
    private static partial Regex FullName();

    [GeneratedRegex(@"^[A-Za-z0-9.\-]{3,50}_[0-9a-hjkmnp-tv-z]{13}$", RegexOptions.IgnoreCase)]
    private static partial Regex FamilyName();
}

/// <summary>
/// Reads a YouTube channel as a parent pastes it. The engine matches channels by id ("UC" and 22 more characters);
/// a handle (@name) is stored next to the id so that pages addressed by handle match too (tests/simulation/README.md).
/// </summary>
internal static partial class YouTubeChannelInput
{
    /// <summary>A channel id, or a channel address that contains one (youtube.com/channel/UC…).</summary>
    public static bool TryParseChannelId(string? input, out string channelId, out string? error)
    {
        channelId = "";
        error = null;
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = Strings.Validation_Empty;
            return false;
        }
        if (ChannelId().Match(text) is { Success: true } id)
        {
            channelId = id.Groups[1].Value;
            return true;
        }
        error = HandleInAddress().IsMatch(text) || text.StartsWith('@') ? Strings.Validation_ChannelHandleOnly : Strings.Validation_ChannelId;
        return false;
    }

    /// <summary>An optional handle: empty gives null; "name", "@name" or a youtube.com/@name address give "@name" in lower case.</summary>
    public static bool TryNormalizeHandle(string? input, out string? handle, out string? error)
    {
        handle = null;
        error = null;
        var text = (input ?? "").Trim();
        if (text.Length == 0) return true;
        if (HandleInAddress().Match(text) is { Success: true } address) text = address.Groups[1].Value;
        if (!text.StartsWith('@')) text = "@" + text;
        if (!Handle().IsMatch(text))
        {
            error = Strings.Validation_ChannelHandle;
            return false;
        }
        handle = text.ToLowerInvariant();
        return true;
    }

    [GeneratedRegex(@"^(?:(?:https?://)?(?:www\.|m\.)?youtube\.com/channel/)?(UC[A-Za-z0-9_-]{22})(?:[/?#].*)?$")]
    private static partial Regex ChannelId();

    [GeneratedRegex(@"^(?:https?://)?(?:www\.|m\.)?youtube\.com/(@[^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HandleInAddress();

    [GeneratedRegex(@"^@[A-Za-z0-9._-]{3,30}$")]
    private static partial Regex Handle();
}

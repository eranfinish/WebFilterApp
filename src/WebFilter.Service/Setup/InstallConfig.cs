using System.Text.Json;
using System.Text.Json.Nodes;
using WebFilter.Service.Policies;

namespace WebFilter.Service.Setup;

/// <summary>
/// The files the installer has the service write next to itself (MVP-PLAN §4.6): <c>appsettings.json</c> with the role
/// chosen during setup, and the native messaging host manifest that the HKLM host keys of Chrome and Edge point at.
/// Generated rather than shipped, because the role and (until spike S1) the extension ids are known only at install time.
/// </summary>
public static class InstallConfig
{
    public const string AppSettingsFileName = "appsettings.json";
    public const string HostExeFileName = "WebFilter.NativeHost.exe";
    public const string HostManifestFileName = BrowserPolicies.HostName + ".json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>"hub" or "agent" (any case, surrounding spaces ignored); null for anything else.</summary>
    public static string? NormalizeRole(string? role) => role?.Trim().ToLowerInvariant() is "hub" or "agent" ? role.Trim().ToLowerInvariant() : null;

    /// <summary>Chrome Web Store and Edge Add-ons ids: 32 letters from a to p.</summary>
    public static bool IsExtensionId(string? id) => id is { Length: 32 } && id.All(c => c is >= 'a' and <= 'p');

    /// <summary>
    /// The installed service's settings: its role, browser policies on (the service runs as LocalSystem), and the
    /// store ids when known — without them no force-install policy is written and the host admits no extension.
    /// </summary>
    public static string AppSettings(string role, ExtensionIds ids)
    {
        var settings = new JsonObject { ["Role"] = role, ["ManageBrowserPolicies"] = true };
        if (ids.ChromeId is { } chrome) settings["ChromeExtensionId"] = chrome;
        if (ids.EdgeId is { } edge) settings["EdgeExtensionId"] = edge;
        var root = new JsonObject
        {
            ["Logging"] = new JsonObject
            {
                ["LogLevel"] = new JsonObject { ["Default"] = "Information", ["Microsoft.Hosting.Lifetime"] = "Information" },
            },
            ["WebFilter"] = settings,
        };
        return root.ToJsonString(Indented) + Environment.NewLine;
    }

    /// <summary>The native messaging host manifest: only our store extensions may start the host.</summary>
    public static string HostManifest(string hostExePath, ExtensionIds ids)
    {
        var origins = new JsonArray();
        foreach (var id in new[] { ids.ChromeId, ids.EdgeId }.OfType<string>().Distinct()) origins.Add($"chrome-extension://{id}/");
        var manifest = new JsonObject
        {
            ["name"] = BrowserPolicies.HostName,
            ["description"] = "WebFilter native messaging host",
            ["path"] = hostExePath,
            ["type"] = "stdio",
            ["allowed_origins"] = origins,
        };
        return manifest.ToJsonString(Indented) + Environment.NewLine;
    }

    /// <summary>Writes both files into <paramref name="directory"/> (the install folder), each replaced atomically.</summary>
    public static void Write(string directory, string role, ExtensionIds ids)
    {
        WriteAtomically(Path.Combine(directory, AppSettingsFileName), AppSettings(role, ids));
        WriteAtomically(Path.Combine(directory, HostManifestFileName), HostManifest(Path.Combine(directory, HostExeFileName), ids));
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}

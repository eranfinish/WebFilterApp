using WebFilter.Service.Policies;

namespace WebFilter.Service.Setup;

/// <summary>
/// <c>WebFilter.Service configure --role hub|agent [--chrome-extension-id id] [--edge-extension-id id] [--dir folder]</c>:
/// writes <see cref="InstallConfig"/>'s files into the install folder (by default the folder of this executable). Run by
/// the installer on install, repair and upgrade, before the service starts.
/// </summary>
internal static class ConfigureCommand
{
    private const string Usage = "Usage: WebFilter.Service configure --role hub|agent [--chrome-extension-id id] [--edge-extension-id id] [--dir folder]";

    public static int Run(string[] args)
    {
        // The installer passes properties that are not set as "", so an empty or missing value means "not set".
        string? Arg(string name)
        {
            var value = args.SkipWhile(a => a != name).Skip(1).FirstOrDefault()?.Trim();
            return string.IsNullOrEmpty(value) || value.StartsWith("--", StringComparison.Ordinal) ? null : value;
        }
        var role = InstallConfig.NormalizeRole(Arg("--role"));
        var ids = new ExtensionIds(Arg("--chrome-extension-id")?.ToLowerInvariant(), Arg("--edge-extension-id")?.ToLowerInvariant());
        if (role is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        foreach (var id in new[] { ids.ChromeId, ids.EdgeId }.OfType<string>().Where(id => !InstallConfig.IsExtensionId(id)))
        {
            Console.Error.WriteLine($"'{id}' is not an extension id (32 letters a-p).");
            return 2;
        }

        var directory = Path.GetFullPath(Arg("--dir") ?? AppContext.BaseDirectory);
        try
        {
            InstallConfig.Write(directory, role, ids);
            Console.WriteLine($"Configured the {role} role in {directory} (Chrome extension: {ids.ChromeId ?? "none"}, Edge extension: {ids.EdgeId ?? "none"}).");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not write the configuration: {ex.Message}");
            return 1;
        }
    }
}

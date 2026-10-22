using WebFilter.Core.Engine;

namespace WebFilter.Service.Network;

/// <summary>Where the programs behind the family's Windows features (PROTECTION-SPEC §2) are on this computer.</summary>
public static class WindowsProgramPaths
{
    /// <summary>
    /// svchost.exe in the system folder, and the machine-wide OneDrive in Program Files — plus the Program Files (x86)
    /// copy when it exists (older installs). Never the per-user OneDrive under %LOCALAPPDATA%.
    /// </summary>
    /// <param name="exists">Whether a file exists (File.Exists; replaced in tests).</param>
    public static WindowsPrograms Resolve(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var oneDrive = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles)) oneDrive.Add(Path.Combine(programFiles, "Microsoft OneDrive", "OneDrive.exe"));
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(programFilesX86) && !string.Equals(programFilesX86, programFiles, StringComparison.OrdinalIgnoreCase))
        {
            var x86 = Path.Combine(programFilesX86, "Microsoft OneDrive", "OneDrive.exe");
            if (exists(x86)) oneDrive.Add(x86);
        }
        return new WindowsPrograms(Path.Combine(Environment.SystemDirectory, "svchost.exe"), oneDrive);
    }
}

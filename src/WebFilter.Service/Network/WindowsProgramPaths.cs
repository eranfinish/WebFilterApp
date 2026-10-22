using WebFilter.Core.Engine;

namespace WebFilter.Service.Network;

/// <summary>
/// Where the machine-wide programs restricted accounts may use are on this computer: Chrome and Edge, and the programs
/// behind the family's Windows features (PROTECTION-SPEC §2).
/// </summary>
public static class WindowsProgramPaths
{
    /// <summary>
    /// svchost.exe in the system folder; the machine-wide OneDrive and Chrome in Program Files, and Edge in Program Files
    /// (x86) — each plus its copy in the other Program Files folder when that copy exists (older or newer installs). Never
    /// a per-user install under %LOCALAPPDATA%.
    /// </summary>
    /// <param name="exists">Whether a file exists (File.Exists; replaced in tests).</param>
    public static WindowsPrograms Resolve(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var oneDrive = MachineWide(programFiles, programFilesX86, @"Microsoft OneDrive\OneDrive.exe");
        var browsers = MachineWide(programFiles, programFilesX86, @"Google\Chrome\Application\chrome.exe")
            .Concat(MachineWide(programFilesX86, programFiles, @"Microsoft\Edge\Application\msedge.exe")).ToList();
        return new WindowsPrograms(Path.Combine(Environment.SystemDirectory, "svchost.exe"), oneDrive, browsers);

        // The usual folder's copy always (WFP skips a program that is not installed and adds it once it is); the other
        // Program Files folder's copy only when it exists.
        List<string> MachineWide(string usual, string other, string relative)
        {
            var paths = new List<string>();
            if (!string.IsNullOrEmpty(usual)) paths.Add(Path.Combine(usual, relative));
            if (!string.IsNullOrEmpty(other) && !string.Equals(other, usual, StringComparison.OrdinalIgnoreCase) && exists(Path.Combine(other, relative)))
                paths.Add(Path.Combine(other, relative));
            return paths;
        }
    }
}

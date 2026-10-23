using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WebFilter.Notifier;

/// <summary>
/// Opens the parents' admin app from a toast or the tray menu. The admin app's manifest requires administrator rights,
/// so starting it through the shell is what shows the UAC prompt; the notifier itself never elevates.
/// </summary>
public sealed class AdminLauncher(string adminPath, Func<ProcessStartInfo, Process?> start, INotifierLog log)
{
    public const string AdminExeName = "WebFilter.Admin.exe";

    /// <summary>ERROR_CANCELLED: the user said no to the UAC prompt.</summary>
    private const int Cancelled = 1223;

    /// <summary>The admin app installed next to the notifier (both in %ProgramFiles%\WebFilter).</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, AdminExeName);

    public string AdminPath { get; } = adminPath;

    /// <summary>Starts the admin app; false when it did not start (the prompt was declined, or the file is missing).</summary>
    public bool Open()
    {
        try
        {
            // The shell, not CreateProcess: only ShellExecute raises the UAC prompt a requireAdministrator manifest needs.
            using var process = start(new ProcessStartInfo(AdminPath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(AdminPath) ?? "" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            log.Write($"The admin app {AdminPath} could not be started: {ex.Message}");
            return false;
        }
    }
}

/// <summary>One notifier per Windows session: a named mutex in the session's own (Local\) namespace.</summary>
public sealed class SingleInstance : IDisposable
{
    public const string DefaultName = @"Local\WebFilter.Notifier";

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>This process's hold on the session, or null when another notifier already runs in it.</summary>
    public static SingleInstance? TryAcquire(string name = DefaultName)
    {
        var mutex = new Mutex(initiallyOwned: true, name, out var createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

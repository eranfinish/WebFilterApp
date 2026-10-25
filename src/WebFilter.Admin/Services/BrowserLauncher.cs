using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WebFilter.Admin.Services;

/// <summary>Opens a web address in the parent's own browser (the requests page's "open for review").</summary>
public interface IBrowserLauncher
{
    /// <summary>Hands <paramref name="address"/> (http or https) to the browser; false when that failed and nothing opened.</summary>
    bool TryOpen(Uri address);
}

/// <summary>
/// Opens an address in the signed-in user's default browser, <b>unelevated</b>. The admin app runs elevated (its Release
/// manifest requires administrator rights), and whatever it starts inherits that token: opening the address through
/// ShellExecute from here would start the browser elevated — every page with administrator rights, and possibly
/// outside the parent's usual browser profile. Instead the address goes to explorer.exe, which hands it to the shell
/// already running on the desktop (the parent's own, unelevated explorer) and exits; the shell opens it with the default
/// browser as the parent normally would. The parent's own browsing is not YouTube-filtered (baseline protection, §13),
/// so the channel or site shows as the child would see it once approved.
/// </summary>
/// <remarks>
/// explorer.exe is started by its full path in the Windows folder, never found through PATH (this process is elevated).
/// The address is one argument, with commas escaped (explorer's own command line splits on them). When no desktop shell
/// runs (a replaced shell, explorer crashed), explorer.exe would start as a new shell of its own; Windows then opens the
/// address from that process — the rare case this does not cover.
/// </remarks>
public sealed class ShellBrowserLauncher(Func<ProcessStartInfo, Process?>? start = null) : IBrowserLauncher
{
    private readonly Func<ProcessStartInfo, Process?> _start = start ?? Process.Start;

    public static string ExplorerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>How the address is handed over: explorer.exe with the address as its only argument; null for anything but http(s).</summary>
    public static ProcessStartInfo? StartInfo(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!address.IsAbsoluteUri || (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp)) return null;
        var info = new ProcessStartInfo(ExplorerPath) { UseShellExecute = false };
        info.ArgumentList.Add(address.AbsoluteUri.Replace(",", "%2C", StringComparison.Ordinal));
        return info;
    }

    public bool TryOpen(Uri address)
    {
        if (StartInfo(address) is not { } info) return false;
        try
        {
            using var process = _start(info);
            return process is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }
}

using System.ServiceProcess;

namespace WebFilter.Service.Hub;

/// <summary>
/// <c>WebFilter.Service pair --hub host:port --code XXXX-XXXX-XXXX [--device id] [--data dir]</c>: pairs this computer
/// with the family hub (run by the installer or by an administrator), then exits. Also pairs an agent again, with another
/// hub (a restored or replaced parents' computer): the new pairing replaces the old one, and a running service is restarted
/// so it syncs with the new hub at once.
/// </summary>
internal static class PairCommand
{
    private static readonly TimeSpan RestartTimeout = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(string[] args)
    {
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        var hub = Arg("--hub");
        var code = Arg("--code");
        if (hub is null || code is null)
        {
            Console.Error.WriteLine("Usage: WebFilter.Service pair --hub host:port --code XXXX-XXXX-XXXX [--device id] [--data dir]");
            return 2;
        }
        var defaults = new ServiceOptions();
        var device = Arg("--device") ?? defaults.DeviceId;
        var data = Arg("--data") ?? defaults.DataDirectory;
        try
        {
            var link = await HubLink.PairAsync(data, hub.Contains(':') ? hub : $"{hub}:{Contracts.Hub.HubApi.DefaultPort}", code, device, Environment.MachineName);
            Console.WriteLine($"Paired {device} with hub {link.HubId} at {link.Address}.");
            // The running service read its pairing when it started. Not for another data folder (tests, a second copy).
            if (OperatingSystem.IsWindows() && string.Equals(Path.GetFullPath(data), Path.GetFullPath(defaults.DataDirectory), StringComparison.OrdinalIgnoreCase))
                RestartRunningService();
            return 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Pairing failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or System.Security.Authentication.AuthenticationException)
        {
            // Unreachable hub: the commonest causes at home are a hub that is off or a network Windows marks as Public
            // (the hub's firewall rule is for Private networks only).
            Console.Error.WriteLine($"Pairing failed: the hub at {hub} did not answer ({ex.Message}). " +
                "Check that the hub computer is on, on the same network, and that the network is set to Private on both computers.");
            return 1;
        }
    }

    /// <summary>
    /// Restarts the installed service when it runs (the installer pairs before it starts, so then there is nothing to do).
    /// A failure is reported, not fatal: the pairing is saved and takes effect at the next start.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestartRunningService()
    {
        try
        {
            using var service = new ServiceController(ServiceOptions.WindowsServiceName);
            if (service.Status != ServiceControllerStatus.Running) return;
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, RestartTimeout);
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, RestartTimeout);
            Console.WriteLine("Restarted the WebFilter service; it syncs with the new hub now.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.ServiceProcess.TimeoutException)
        {
            Console.Error.WriteLine($"Paired, but the WebFilter service could not be restarted ({ex.Message}). Restart it (Restart-Service WebFilter) or the computer.");
        }
    }
}

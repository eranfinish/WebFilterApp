namespace WebFilter.Service.Hub;

/// <summary>
/// <c>WebFilter.Service pair --hub host:port --code XXXX-XXXX-XXXX [--device id] [--data dir]</c>: pairs this computer
/// with the family hub (run by the installer or by an administrator), then exits.
/// </summary>
internal static class PairCommand
{
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
}

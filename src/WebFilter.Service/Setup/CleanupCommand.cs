using WebFilter.Service.Policies;

namespace WebFilter.Service.Setup;

/// <summary>
/// <c>WebFilter.Service cleanup</c>: the uninstall custom action (<see cref="MachineCleanup"/>) — run as LocalSystem by the
/// installer after it stops the service, or by an administrator after a failed uninstall. Exits 0 when everything was
/// removed, 1 when something was left (listed on the output, which the installer writes to its log).
/// </summary>
internal static class CleanupCommand
{
    public static int Run()
    {
        var log = Console.Out;
        using var hives = new RegistryUserHives(log);
        // The native WFP engine (WebFilter.Wfp.Native) is not built yet, so there are no filters to remove; pass it here once it exists.
        var cleanup = new MachineCleanup(new RegistryPolicyStore(), hives, wfp: null, log);
        var result = cleanup.Run(ServiceOptions.MachineDataRoot, Path.Combine(new ServiceOptions().DataDirectory, PolicyService.StateFileName));
        return result.Complete ? 0 : 1;
    }
}

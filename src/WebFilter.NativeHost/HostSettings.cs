namespace WebFilter.NativeHost;

/// <summary>
/// Where the host connects and reads profile caches. Release builds use fixed values only: an environment override
/// would let a child point their own host at a fake service. Simulation builds accept overrides for tests.
/// </summary>
/// <param name="SimulateStandardUser">
/// Simulation only (<c>WEBFILTER_SIMULATE_STANDARD_USER=1</c>): the fallback treats the account as a standard user even
/// if it is an administrator, so level-3 runs on a developer's admin account exercise a child's service-down path.
/// </param>
internal sealed record HostSettings(string AgentPipeName, string ProfileDirectory, bool RequireSystemServer, bool SimulateStandardUser = false)
{
    private static readonly string DefaultProfiles =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WebFilter", "profiles");

    public static HostSettings Load()
    {
#if SIMULATION
        return new HostSettings(
            Environment.GetEnvironmentVariable("WEBFILTER_AGENT_PIPE") ?? "WebFilter.Agent",
            Environment.GetEnvironmentVariable("WEBFILTER_PROFILE_DIR") ?? DefaultProfiles,
            RequireSystemServer: false,
            SimulateStandardUser: Environment.GetEnvironmentVariable("WEBFILTER_SIMULATE_STANDARD_USER") == "1");
#else
        return new HostSettings("WebFilter.Agent", DefaultProfiles, RequireSystemServer: true);
#endif
    }
}

namespace WebFilter.NativeHost;

/// <summary>
/// Where the host connects and reads profile caches. Release builds use fixed values only: an environment override
/// would let a child point their own host at a fake service. Simulation builds accept overrides for tests.
/// </summary>
internal sealed record HostSettings(string AgentPipeName, string ProfileDirectory, bool RequireSystemServer)
{
    private static readonly string DefaultProfiles =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WebFilter", "profiles");

    public static HostSettings Load()
    {
#if SIMULATION
        return new HostSettings(
            Environment.GetEnvironmentVariable("WEBFILTER_AGENT_PIPE") ?? "WebFilter.Agent",
            Environment.GetEnvironmentVariable("WEBFILTER_PROFILE_DIR") ?? DefaultProfiles,
            RequireSystemServer: false);
#else
        return new HostSettings("WebFilter.Agent", DefaultProfiles, RequireSystemServer: true);
#endif
    }
}

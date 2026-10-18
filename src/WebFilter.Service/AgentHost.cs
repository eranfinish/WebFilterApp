namespace WebFilter.Service;

// Placeholder host for the agent/hub roles. Components (pipes, DecisionService,
// UsageTracker, PolicyManager, WfpManager, HubSync/HubServer) arrive in M2+.
public sealed class AgentHost(ILogger<AgentHost> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("WebFilter service started");
        stoppingToken.Register(() => logger.LogInformation("WebFilter service stopping"));
        return Task.CompletedTask;
    }
}

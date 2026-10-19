using Microsoft.Extensions.Hosting.WindowsServices;

namespace WebFilter.Service;

/// <summary>
/// Makes Windows' service recovery (restart on failure, set by the installer) fire when a <see cref="BackgroundService"/> fails.
/// By default the host then stops cleanly (<see cref="BackgroundServiceExceptionBehavior.StopHost"/>) and
/// <c>WindowsServiceLifetime</c> reports SERVICE_STOPPED with exit code 0, which Windows does not count as a failure. So when
/// running as a Windows service the host ignores the failure and <see cref="FaultExitWatcher"/> ends the process with
/// <see cref="ExitCode"/> while the service is still running — Microsoft's documented fix, in one place. Console runs and tests
/// keep the host's default.
/// </summary>
public static class FaultExit
{
    public const int ExitCode = 1;

    public static IServiceCollection AddFaultExit(this IServiceCollection services) =>
        services.AddFaultExit(WindowsServiceHelpers.IsWindowsService(), Environment.Exit);

    internal static IServiceCollection AddFaultExit(this IServiceCollection services, bool windowsService, Action<int> exit)
    {
        if (!windowsService) return services;
        services.Configure<HostOptions>(options => options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        services.AddHostedService(sp => ActivatorUtilities.CreateInstance<FaultExitWatcher>(sp, exit));
        return services;
    }
}

/// <summary>
/// Once the host has started, watches every <see cref="BackgroundService"/>: one that ends faulted or canceled while no stop
/// was requested (what the host itself calls a failure) ends the process. A failure during a stop that Windows requested is
/// left to the host's clean stop, so the service can always be stopped.
/// </summary>
/// <remarks>
/// <c>exit</c> runs on a thread-pool thread, never inside a host callback or a service's ExecuteAsync, and the host is not
/// stopping, so nothing that a ProcessExit handler could wait for is blocked on that thread. .NET 10's ConsoleLifetime (also
/// used by the hub's Kestrel host) registers no ProcessExit handler, and WindowsServiceLifetime none either.
/// </remarks>
internal sealed class FaultExitWatcher(IServiceProvider services, IHostApplicationLifetime lifetime, ILogger<FaultExitWatcher> log, Action<int> exit)
    : IHostedService
{
    private int _exiting;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // After every StartAsync, so each ExecuteTask exists; a service that already failed is caught by its continuation.
        lifetime.ApplicationStarted.Register(Watch);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Watch()
    {
        foreach (var service in services.GetServices<IHostedService>().OfType<BackgroundService>())
        {
            service.ExecuteTask?.ContinueWith(task => OnEnded(service, task), CancellationToken.None,
                TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
        }
    }

    private void OnEnded(BackgroundService service, Task task)
    {
        if (lifetime.ApplicationStopping.IsCancellationRequested || Interlocked.Exchange(ref _exiting, 1) == 1) return;
        log.LogCritical(task.Exception?.GetBaseException(),
            "{Service} stopped unexpectedly; ending the process with exit code {ExitCode} so Windows restarts the service",
            service.GetType().Name, FaultExit.ExitCode);
        exit(FaultExit.ExitCode);
    }
}

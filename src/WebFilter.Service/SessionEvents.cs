using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace WebFilter.Service;

/// <summary>
/// Users logging on to Windows, as the service control manager reports them (SERVICE_CONTROL_SESSIONCHANGE; only when
/// running as the Windows service). Windows has normally loaded the user's registry hive by then, so their HKEY_USERS
/// policies can be written before they open a browser (MVP-PLAN §5.2).
/// </summary>
public sealed class SessionEvents
{
    /// <summary>A user logged on to the session with this id. Raised on a thread-pool thread.</summary>
    public event Action<int>? LoggedOn;

    public void OnSessionChange(SessionChangeReason reason, int sessionId)
    {
        if (reason == SessionChangeReason.SessionLogon) LoggedOn?.Invoke(sessionId);
    }
}

public static class SessionEventsServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SessionEvents"/>. As a Windows service, also replaces the lifetime that
    /// <c>AddWindowsService</c> registered (so call it after that) with one that asks for session changes.
    /// </summary>
    public static IServiceCollection AddSessionEvents(this IServiceCollection services) =>
        services.AddSessionEvents(WindowsServiceHelpers.IsWindowsService());

    internal static IServiceCollection AddSessionEvents(this IServiceCollection services, bool windowsService)
    {
        services.AddSingleton<SessionEvents>();
        if (windowsService)
        {
            services.RemoveAll<IHostLifetime>();
            services.AddSingleton<IHostLifetime, SessionAwareServiceLifetime>();
        }
        return services;
    }
}

/// <summary>
/// <see cref="WindowsServiceLifetime"/> that also accepts session-change notifications (the stock one accepts only stop and
/// shutdown) and passes them to <see cref="SessionEvents"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SessionAwareServiceLifetime : WindowsServiceLifetime
{
    private readonly SessionEvents _sessions;

    public SessionAwareServiceLifetime(IHostEnvironment environment, IHostApplicationLifetime applicationLifetime, ILoggerFactory loggerFactory,
        IOptions<HostOptions> hostOptions, IOptions<WindowsServiceLifetimeOptions> serviceOptions, SessionEvents sessions)
        : base(environment, applicationLifetime, loggerFactory, hostOptions, serviceOptions)
    {
        _sessions = sessions;
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription) =>
        _sessions.OnSessionChange(changeDescription.Reason, changeDescription.SessionId);
}

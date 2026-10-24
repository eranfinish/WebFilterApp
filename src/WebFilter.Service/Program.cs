using WebFilter.Service;
using WebFilter.Service.Accounts;
using WebFilter.Service.Guard;
using WebFilter.Service.Hub;
using WebFilter.Service.Network;
using WebFilter.Service.Pipes;
using WebFilter.Service.Policies;
using WebFilter.Service.Setup;

// Commands for the installer and administrators; without one, run as the service.
switch (args.FirstOrDefault())
{
    case "pair": return await PairCommand.RunAsync(args[1..]);
    case "configure": return ConfigureCommand.Run(args[1..]);
    case "cleanup": return CleanupCommand.Run();
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = ServiceOptions.WindowsServiceName);
builder.Services.AddFaultExit();
builder.Services.Configure<ServiceOptions>(builder.Configuration.GetSection("WebFilter"));
#if SIMULATION
builder.Services.AddSingleton<TimeProvider>(new SimulationClock(TimeProvider.System.GetUtcNow()));
#else
builder.Services.AddSingleton(TimeProvider.System);
#endif
builder.Services.AddSingleton<FamilyService>();
builder.Services.AddSingleton<ILocalAccounts, WindowsLocalAccounts>();
builder.Services.AddSingleton<IMachineProbe, WindowsMachineProbe>();
builder.Services.AddHostedService<TickService>();
// The extension guard (PROTECTION-SPEC §14): the agent pipe reports the extension's sessions, the guard decides which
// browsers to block, and WfpService leaves them out of the allowlists.
builder.Services.AddSingleton<ExtensionConnections>();
builder.Services.AddSingleton<BrowserBlocks>();
builder.Services.AddHostedService<AgentPipeServer>();
builder.Services.AddHostedService<AdminPipeServer>();
builder.Services.AddHostedService<NotifyPipeServer>();
// After AddWindowsService: as a Windows service its lifetime is replaced by one that also reports user logons.
builder.Services.AddSessionEvents();
builder.Services.AddHostedService<PolicyService>();
builder.Services.AddHostedService<WfpService>();
builder.Services.AddHostedService<ExtensionGuardService>();
builder.Services.AddSingleton<HubServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubServer>());
builder.Services.AddSingleton<HubSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubSyncService>());

var host = builder.Build();
var family = host.Services.GetRequiredService<FamilyService>();
family.Start();
// A requested stop counts as clean from its first moment (Windows may end a slow shutdown); a crash or kill does not.
host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(family.StoppingCleanly);
host.Run();
return 0;

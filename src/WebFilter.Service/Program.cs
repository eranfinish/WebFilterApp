using WebFilter.Service;
using WebFilter.Service.Hub;
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
builder.Services.Configure<ServiceOptions>(builder.Configuration.GetSection("WebFilter"));
#if SIMULATION
builder.Services.AddSingleton<TimeProvider>(new SimulationClock(TimeProvider.System.GetUtcNow()));
#else
builder.Services.AddSingleton(TimeProvider.System);
#endif
builder.Services.AddSingleton<FamilyService>();
builder.Services.AddHostedService<TickService>();
builder.Services.AddHostedService<AgentPipeServer>();
builder.Services.AddHostedService<AdminPipeServer>();
builder.Services.AddHostedService<PolicyService>();
builder.Services.AddSingleton<HubServer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubServer>());
builder.Services.AddSingleton<HubSyncService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HubSyncService>());

var host = builder.Build();
host.Services.GetRequiredService<FamilyService>().Start();
host.Run();
return 0;

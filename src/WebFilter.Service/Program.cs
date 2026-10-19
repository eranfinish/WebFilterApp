using WebFilter.Service;
using WebFilter.Service.Pipes;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "WebFilter");
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

var host = builder.Build();
host.Services.GetRequiredService<FamilyService>().Start();
host.Run();

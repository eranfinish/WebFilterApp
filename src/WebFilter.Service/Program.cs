using WebFilter.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "WebFilter");
builder.Services.AddHostedService<AgentHost>();

var host = builder.Build();
host.Run();

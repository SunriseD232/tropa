using Tropa.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Tropa");
builder.Services.AddHostedService<ServiceHost>();

var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);

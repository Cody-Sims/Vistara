using Vistara.Worker.Composition.Media;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Composition.Runtime;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddVistaraMedia(builder.Configuration);
builder.Services.AddVistaraWorkerPlatform(builder.Configuration);
builder.Services.AddVistaraWorkerRuntime(builder.Configuration);

IHost host = builder.Build();
host.Services.ValidateVistaraWorkerPlatformComposition();
await host.RunAsync();

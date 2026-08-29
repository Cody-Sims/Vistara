using Vistara.Worker.Composition.Media;
using Vistara.Worker.Composition.Platform;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddVistaraMedia(builder.Configuration);
builder.Services.AddVistaraWorkerPlatform(builder.Configuration);

IHost host = builder.Build();
host.Services.ValidateVistaraWorkerPlatformComposition();
await host.RunAsync();

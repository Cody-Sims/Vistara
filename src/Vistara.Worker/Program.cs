HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
IHost host = builder.Build();

await host.RunAsync();

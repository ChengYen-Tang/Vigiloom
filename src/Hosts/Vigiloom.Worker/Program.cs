using Vigiloom.Worker.Hosting;

var builder = WorkerHost.CreateBuilder(args);
using var host = builder.Build();
await host.RunAsync();

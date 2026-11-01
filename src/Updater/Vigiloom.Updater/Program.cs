using Vigiloom.Updater.Hosting;

var builder = UpdaterHost.CreateBuilder(args);
using var host = builder.Build();
await host.RunAsync();

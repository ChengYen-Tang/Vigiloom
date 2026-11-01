using Vigiloom.Agent.Hosting;

var builder = AgentHost.CreateBuilder(args);
using var host = builder.Build();
await host.RunAsync();

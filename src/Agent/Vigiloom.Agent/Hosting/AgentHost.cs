using Microsoft.Extensions.Hosting;

namespace Vigiloom.Agent.Hosting;

/// <summary>Composition root; no collection, update or scheduled work is enabled yet.</summary>
public static class AgentHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "Vigiloom Agent");
        builder.Services.AddSystemd();
        return builder;
    }
}

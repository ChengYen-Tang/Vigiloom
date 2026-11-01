using Microsoft.Extensions.Hosting;

namespace Vigiloom.Worker.Hosting;

/// <summary>Composition root; no collection, update or scheduled work is enabled yet.</summary>
public static class WorkerHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "Vigiloom Worker");
        builder.Services.AddSystemd();
        return builder;
    }
}

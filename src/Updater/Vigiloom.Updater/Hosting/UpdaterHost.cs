using Microsoft.Extensions.Hosting;

namespace Vigiloom.Updater.Hosting;

/// <summary>Composition root; no collection, update or scheduled work is enabled yet.</summary>
public static class UpdaterHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "Vigiloom Updater");
        builder.Services.AddSystemd();
        return builder;
    }
}

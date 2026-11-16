using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Vigiloom.Updater.Hosting;

/// <summary>Composition root for the independent updater service.</summary>
public static class UpdaterHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "Vigiloom Updater");
        builder.Services.AddSystemd();
        builder.Services.AddHostedService<RecoveryWorker>();
        return builder;
    }
}

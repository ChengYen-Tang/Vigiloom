using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Microsoft.Extensions.Hosting;

namespace Vigiloom.Worker.Hosting;

/// <summary>Composition root; no collection, update or scheduled work is enabled yet.</summary>
public static class WorkerHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddModuleSqlServer<IdentityAccessDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "identity");
        builder.Services.AddModuleSqlServer<ResourcesDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "resources");
        builder.Services.AddModuleSqlServer<NetworkDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "network");
        builder.Services.AddWindowsService(options => options.ServiceName = "Vigiloom Worker");
        builder.Services.AddSystemd();
        return builder;
    }
}

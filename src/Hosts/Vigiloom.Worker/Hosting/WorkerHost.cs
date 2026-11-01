using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Microsoft.Extensions.Hosting;
using Hangfire;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.NetworkMonitoring.Application;

namespace Vigiloom.Worker.Hosting;

/// <summary>Composition root for the background scheduler and module services.</summary>
public static class WorkerHost
{
    public static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddModuleSqlServer<IdentityAccessDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "identity");
        builder.Services.AddModuleSqlServer<ResourcesDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "resources");
        builder.Services.AddModuleSqlServer<NetworkDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "network");
        builder.Services.AddModuleSqlServer<SchedulingDbContext>(_ => SchedulingRegistration.Connection(builder.Configuration), "scheduling");
        builder.Services.AddWindowsService(options => options.ServiceName = "Vigiloom Worker");
        builder.Services.AddSystemd();
        builder.Services.AddVigiloomScheduling(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<IDevicePingSource, DevicePingSource>();
        builder.Services.AddScoped<PingSettings>();
        builder.Services.AddScoped<PingStore>();
        builder.Services.AddScoped<IPingProbe, SystemPingProbe>();
        builder.Services.AddScoped<PingRunner>();
        builder.Services.AddHostedService<PingWorker>();
        builder.Services.AddHostedService<OutboxSchedule>();
        builder.Services.AddHangfireServer();
        return builder;
    }
}

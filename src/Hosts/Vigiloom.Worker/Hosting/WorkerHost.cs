using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Contracts.Operations;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
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
        builder.Services.AddModuleSqlServer<NetworkDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "network", pooled: true);
        builder.Services.AddModuleSqlServer<SchedulingDbContext>(_ => SchedulingRegistration.Connection(builder.Configuration), "scheduling");
        builder.Services.AddModuleSqlServer<OperationsDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "operations");
        builder.Services.AddScoped<IOperationAuditWriter<OperationAudit>, OperationAuditWriter>();
        builder.Services.AddWindowsService(options => options.ServiceName = "Vigiloom Worker");
        builder.Services.AddSystemd();
        builder.Services.AddVigiloomScheduling(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<WorkerDiagnosticsStore>();
        builder.Services.AddSingleton<WorkerDiagnostics>();
        builder.Services.AddHostedService<WorkerDiagnosticsPublisher>();
        builder.Services.AddScoped<IDevicePingSource, DevicePingSource>();
        builder.Services.AddScoped<PingSettings>();
        builder.Services.AddScoped<PingStore>();
        builder.Services.AddScoped<IPingProbe, SystemPingProbe>();
        builder.Services.AddScoped<PingRunner>();
        builder.Services.AddScoped<SpeedSettings>();
        builder.Services.AddScoped<SpeedQueue>();
        builder.Services.AddScoped<SpeedRunner>();
        builder.Services.AddHttpClient(SpeedtestNetProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(SpeedtestNetProbe.CreatePrimaryHandler);
        builder.Services.AddScoped<ISpeedProbe, SpeedtestNetProbe>();
        builder.Services.AddScoped<AutomaticSpeedJob>();
        builder.Services.AddHostedService<SpeedWorker>();
        builder.Services.AddHostedService<SpeedSchedule>();
        builder.Services.AddHostedService<PingWorker>();
        builder.Services.AddHostedService<OutboxSchedule>();
        builder.Services.AddHangfireServer();
        return builder;
    }
}

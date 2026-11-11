using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Contracts.Operations;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Security;
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
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

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
        builder.Services.AddModuleSqlServer<ServiceObservabilityDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "service", pooled: true);
        builder.Services.AddModuleSqlServer<AgentManagementDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "agent", pooled: true);
        builder.Services.AddModuleSqlServer<SecurityAnalysisDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "security", pooled: true);
        builder.Services.AddVigiloomDataProtection(builder.Configuration);
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
        builder.Services.AddScoped<SpeedRetentionCleanup>();
        builder.Services.AddScoped<SpeedRetentionJob>();
        builder.Services.AddScoped<AgentInboxCleanup>();
        builder.Services.AddScoped<UpdateTrustStore>();
        builder.Services.AddScoped<AgentPackageSourceSettings>();
        builder.Services.AddScoped<AgentPackageRefreshJob>();
        builder.Services.AddScoped<AgentInboxRetentionJob>();
        builder.Services.AddScoped<InventoryExpiry>();
        builder.Services.AddScoped<InventoryExpiryJob>();
        builder.Services.AddHttpClient(SpeedtestNetProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(SpeedtestNetProbe.CreatePrimaryHandler);
        builder.Services.AddScoped<ISpeedProbe, SpeedtestNetProbe>();
        builder.Services.AddScoped<AutomaticSpeedJob>();
        builder.Services.AddHostedService<SpeedWorker>();
        builder.Services.AddHostedService<SpeedSchedule>();
        builder.Services.AddHostedService<SpeedRetentionSchedule>();
        builder.Services.AddHostedService<AgentInboxRetentionSchedule>();
        builder.Services.AddHostedService<InventoryExpirySchedule>();
        builder.Services.AddHostedService<AgentPackageRefreshSchedule>();
        builder.Services.AddHostedService<PingWorker>();
        builder.Services.AddSingleton<HealthRunner>();
        builder.Services.AddHostedService<HealthPolicyWorker>();
        builder.Services.AddHostedService<HealthExpiryWorker>();
        builder.Services.AddHostedService<HealthWorker>();
        builder.Services.AddHostedService<OutboxSchedule>();
        builder.Services.AddHangfireServer();
        return builder;
    }
}

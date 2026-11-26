using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Contracts.Operations;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.BuildingBlocks.Retention;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Infrastructure.Operations;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.IdentityAccess.Application.Retention;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Resources.Application.Retention;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Microsoft.Extensions.Hosting;
using Hangfire;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Scheduling.Retention;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Application.Retention;
using Vigiloom.Modules.AgentManagement.Application.HostMetrics;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Retention;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

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
        builder.Services.AddHttpClient<Vigiloom.Modules.AlertingOperations.Application.Line.LinePushClient>();
        builder.Services.AddScoped<Vigiloom.Infrastructure.Scheduling.Outbox.IOutboxHandler, Vigiloom.Modules.AlertingOperations.Application.Line.LineDeliveryHandler>();
        builder.Services.AddScoped<Vigiloom.Modules.AlertingOperations.Application.Line.LineDeliveryQueue>();
        builder.Services.AddScoped<LineDeliveryJob>();
        builder.Services.AddHostedService<LineDeliverySchedule>();
        builder.Services.AddScoped<IOperationAuditWriter<OperationAudit>, OperationAuditWriter>();
        builder.Services.AddScoped<IdentityAuditRetention>();
        builder.Services.AddScoped<IAuditRetentionSource>(provider =>
            string.IsNullOrWhiteSpace(provider.GetRequiredService<IConfiguration>()["Services:Identity:Url"])
                ? provider.GetRequiredService<IdentityAuditRetention>()
                : provider.GetRequiredService<RemoteIdentityAuditRetention>());
        builder.Services.AddHttpClient<RemoteIdentityAuditRetention>((services, client) =>
            client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        builder.Services.AddScoped<IAuditRetentionSource, DeviceAuditRetention>();
        builder.Services.AddScoped<IAuditRetentionSource, DashboardAuditRetention>();
        builder.Services.AddScoped<IAuditRetentionSource, AgentAuditRetention>();
        builder.Services.AddScoped<IAuditRetentionSource, SecurityAuditRetention>();
        builder.Services.AddWindowsService(options => options.ServiceName = "Vigiloom Worker");
        builder.Services.AddSystemd();
        builder.Services.AddVigiloomScheduling(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<WorkerDiagnosticsStore>();
        builder.Services.AddScoped<AuditRetentionSettings>();
        builder.Services.AddScoped<AuditRetentionCleanup>();
        builder.Services.AddScoped<FailedJobRetention>();
        builder.Services.AddScoped<AuditRetentionJob>();
        builder.Services.AddSingleton<WorkerDiagnostics>();
        builder.Services.AddHostedService<WorkerDiagnosticsPublisher>();
        builder.Services.AddScoped<IDevicePingSource, DevicePingSource>();
        builder.Services.AddScoped<IDeviceAgentSource, DeviceAgentSource>();
        builder.Services.AddScoped<Vigiloom.Contracts.Agent.Inventory.IAgentOwnerFence, AgentOwnerFence>();
        builder.Services.AddScoped<Vigiloom.Contracts.Agent.Inventory.IAgentOwnerResolver, AgentOwnerResolver>();
        builder.Services.AddScoped<PingSettings>();
        builder.Services.AddScoped<PingStore>();
        builder.Services.AddScoped<IPingProbe, SystemPingProbe>();
        builder.Services.AddScoped<PingRunner>();
        builder.Services.AddScoped<PingRollupBackfill>();
        builder.Services.AddScoped<PingRollupBackfillJob>();
        builder.Services.AddScoped<PingRetentionCleanup>();
        builder.Services.AddScoped<PingRetentionJob>();
        builder.Services.AddScoped<MetricRollupMaterializer>();
        builder.Services.AddScoped<MetricWindowBackfill>();
        builder.Services.AddScoped<MetricRollupCoordinator>();
        builder.Services.AddScoped<MetricRollupJob>();
        builder.Services.AddScoped<TelemetryRetentionCleanup>();
        builder.Services.AddScoped<IHostLogRetention, HostLogRetention>();
        builder.Services.AddScoped<ISecurityDetectionRetention, SecurityDetectionRetention>();
        builder.Services.AddScoped<TelemetryRetentionJob>();
        builder.Services.AddScoped<SpeedSettings>();
        builder.Services.AddScoped<SpeedQueue>();
        builder.Services.AddScoped<SpeedRunner>();
        builder.Services.AddScoped<SpeedRetentionCleanup>();
        builder.Services.AddScoped<SpeedRetentionJob>();
        builder.Services.AddScoped<AgentInboxCleanup>();
        builder.Services.AddScoped<HostMetricRollupBackfill>();
        builder.Services.AddScoped<HostMetricRollupJob>();
        builder.Services.AddScoped<HostMetricRetentionCleanup>();
        builder.Services.AddScoped<HostMetricRetentionJob>();
        builder.Services.AddScoped<Vigiloom.Contracts.Agent.IHostLogReader, HostLogReader>();
        builder.Services.AddScoped<AgentInboxRetentionJob>();
        builder.Services.AddScoped<InventoryExpiry>();
        builder.Services.AddScoped<IntelligenceRefresh>();
        builder.Services.AddScoped<Vigiloom.Contracts.Network.IPingTransitionSink, Vigiloom.Modules.AlertingOperations.Application.PingAlertSink>();
        if (string.IsNullOrWhiteSpace(builder.Configuration["Services:Operations:Url"]))
            builder.Services.AddScoped<Vigiloom.Contracts.Notifications.INotificationWriter, Vigiloom.Modules.AlertingOperations.Application.NotificationWriter>();
        else
        {
            builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233", builder.Configuration["Temporal:Namespace"] ?? "default");
            builder.Services.AddScoped<TemporalOutboxHandler>();
            builder.Services.AddScoped<Vigiloom.Contracts.Notifications.INotificationWriter>(services =>
                new TemporalNotificationWriter(new OutboxWriter([services.GetRequiredService<TemporalOutboxHandler>()]), services.GetRequiredService<IConfiguration>()));
        }
        builder.Services.AddSingleton<Vigiloom.Contracts.Resources.IServiceGroupSource, Vigiloom.Modules.Resources.Application.ServiceGroupSource>();
        builder.Services.AddSingleton<Vigiloom.Contracts.Services.IHealthTransitionSink, Vigiloom.Modules.AlertingOperations.Application.ServiceHealthAlertSink>();
        builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.IntelligencePublication>();
        builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities.DebianRefresh>();
        builder.Services.AddScoped<DebianRefreshJob>();
        builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities.UbuntuRefresh>();
        builder.Services.AddScoped<UbuntuRefreshJob>();
        builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities.MsrcRefresh>();
        builder.Services.AddScoped<MsrcRefreshJob>();
        builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities.EpssRefresh>();
        builder.Services.AddScoped<EpssRefreshJob>();
        builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities.LinuxReassessment>();
        builder.Services.AddScoped<LinuxReassessmentJob>();
        builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities.WindowsReassessment>();
        builder.Services.AddSingleton<Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities.MicrosoftCatalogCache>();
        builder.Services.AddScoped<WindowsReassessmentJob>();
        builder.Services.AddScoped<AdvisoryRefresh>();
        builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma.SigmaRefresh>();
        builder.Services.AddScoped<SigmaRefreshJob>();
        builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase.SignatureRefresh>();
        builder.Services.AddScoped<SignatureRefreshJob>();
        builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines.BaselineRefresh>();
        builder.Services.AddScoped<BaselineRefreshJob>();
        builder.Services.AddScoped<AdvisoryRefreshJob>();
        builder.Services.AddScoped<Vigiloom.Contracts.Intelligence.IKevCatalog, KevCatalog>();
        builder.Services.AddScoped<Vigiloom.Contracts.Intelligence.IAdvisoryCatalog, AdvisoryCatalog>();
        builder.Services.AddScoped<Vigiloom.Contracts.Intelligence.ISigmaCatalog, Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma.SigmaCatalog>();
        builder.Services.AddHostDetection();
        builder.Services.AddScoped<Vigiloom.Contracts.Intelligence.IBaselineSource, Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines.BaselineSource>();
        builder.Services.AddScoped<IntelligenceRefreshJob>();
        builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Integrity.IntegrityExpiry>();
        builder.Services.AddScoped<IntegrityExpiryJob>();
        builder.Services.AddScoped<InventoryExpiryJob>();
        builder.Services.AddHttpClient(SpeedtestNetProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(SpeedtestNetProbe.CreatePrimaryHandler);
        builder.Services.AddHttpClient(IntelligenceHttp.KevClientName)
            .ConfigurePrimaryHttpMessageHandler(IntelligenceHttp.CreateKevHandler);
        builder.Services.AddHttpClient(IntelligenceHttp.DebianClientName)
            .ConfigurePrimaryHttpMessageHandler(IntelligenceHttp.CreateDebianHandler);
        builder.Services.AddHttpClient(IntelligenceHttp.UbuntuClientName)
            .ConfigurePrimaryHttpMessageHandler(IntelligenceHttp.CreateKevHandler);
        builder.Services.AddHttpClient(IntelligenceHttp.MsrcClientName)
            .ConfigurePrimaryHttpMessageHandler(IntelligenceHttp.CreateKevHandler);
        builder.Services.AddHttpClient(IntelligenceHttp.EpssClientName)
            .ConfigurePrimaryHttpMessageHandler(IntelligenceHttp.CreateKevHandler);
        builder.Services.AddHttpClient(IntelligenceHttp.MicrosoftCatalogClientName)
            .ConfigurePrimaryHttpMessageHandler(IntelligenceHttp.CreateMicrosoftCatalogHandler);
        builder.Services.AddHttpClient(IntelligenceHttp.AdvisoryClientName)
            .ConfigurePrimaryHttpMessageHandler(IntelligenceHttp.CreateAdvisoryHandler);
        builder.Services.AddHttpClient(IntelligenceHttp.RepositoryClientName)
            .ConfigurePrimaryHttpMessageHandler(IntelligenceHttp.CreateAdvisoryHandler);
        builder.Services.AddScoped<ISpeedProbe, SpeedtestNetProbe>();
        builder.Services.AddScoped<AutomaticSpeedJob>();
        if (string.IsNullOrWhiteSpace(builder.Configuration["Services:Network:Url"]))
        {
            builder.Services.AddHostedService<SpeedWorker>();
            builder.Services.AddHostedService<SpeedSchedule>();
            builder.Services.AddHostedService<SpeedRetentionSchedule>();
            builder.Services.AddHostedService<PingWorker>();
            builder.Services.AddHostedService<PingRollupBackfillSchedule>();
            builder.Services.AddHostedService<PingRetentionSchedule>();
        }
        builder.Services.AddHostedService<AgentInboxRetentionSchedule>();
        builder.Services.AddHostedService<InventoryExpirySchedule>();
        builder.Services.AddHostedService<IntelligenceRefreshSchedule>();
        builder.Services.AddHostedService<DebianRefreshSchedule>();
        builder.Services.AddHostedService<UbuntuRefreshSchedule>();
        builder.Services.AddHostedService<MsrcRefreshSchedule>();
        builder.Services.AddHostedService<EpssRefreshSchedule>();
        builder.Services.AddHostedService<LinuxReassessmentSchedule>();
        builder.Services.AddHostedService<WindowsReassessmentSchedule>();
        builder.Services.AddHostedService<AdvisoryRefreshSchedule>();
        builder.Services.AddHostedService<SigmaRefreshSchedule>();
        builder.Services.AddHostedService<SignatureRefreshSchedule>();
        builder.Services.AddHostedService<BaselineRefreshSchedule>();
        builder.Services.AddHostedService<IntegrityExpirySchedule>();
        builder.Services.AddHostedService<AgentPackageRefreshSchedule>();
        builder.Services.AddHostedService<HostMetricRollupSchedule>();
        builder.Services.AddHostedService<HostMetricRetentionSchedule>();
        builder.Services.AddHostedService<TelemetryRetentionSchedule>();
        builder.Services.AddHostedService<AuditRetentionSchedule>();
        builder.Services.AddHostedService<MetricRollupSchedule>();
        builder.Services.AddSingleton<HealthRunner>();
        builder.Services.AddHostedService<HealthPolicyWorker>();
        builder.Services.AddHostedService<HealthExpiryWorker>();
        builder.Services.AddHostedService<HealthWorker>();
        builder.Services.AddHostedService<OutboxSchedule>();
        builder.Services.AddHangfireServer();
        return builder;
    }
}

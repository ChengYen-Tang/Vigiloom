using Vigiloom.Infrastructure.Telemetry;
using Vigiloom.UpdateSecurity.AgentPackages;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Web.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Contracts.Operations;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.BuildingBlocks.Retention;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Infrastructure.Operations;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Application.Retention;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.Resources.Application.Retention;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Application.Retention;
using Vigiloom.Modules.AgentManagement.Application.HostMetrics;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Retention;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Web.Components;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Api;
using MudBlazor.Services;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Scheduling.Retention;
using Vigiloom.Web.Scheduling;
using Vigiloom.Web.Diagnostics;
using Vigiloom.Web.Groups;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Hangfire;
using Hangfire.Dashboard;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Web.LogQuota;
using Vigiloom.Web.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;

var builder = WebApplication.CreateBuilder(args);
builder.AddVigiloomSelfTelemetry("Vigiloom.Web");

if (!string.IsNullOrWhiteSpace(builder.Configuration["Services:Resources:Url"]))
{
    builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233",
        builder.Configuration["Temporal:Namespace"] ?? "default");
    if (string.IsNullOrWhiteSpace(builder.Configuration["Services:Observability:Url"]))
        builder.Services.AddVigiloomTemporalWorker<Vigiloom.Web.Services.ApplicationEventHandler>(
            builder.Configuration["Work:ObservabilityQueue"] ?? "vigiloom.observability.catalog");
}

// Keep malformed minimal-API requests as 400 responses in Development as well as Production.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = false);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization();
builder.Services.AddModuleSqlServer<IdentityAccessDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "identity");
builder.Services.AddModuleSqlServer<ResourcesDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "resources");
builder.Services.AddModuleSqlServer<DeploymentLinksDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "resources", pooled: true, migrationHistory: DeploymentLinksDbContext.MigrationHistory);
builder.Services.AddModuleSqlServer<AgentManagementDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "agent");
builder.Services.AddModuleSqlServer<ServiceObservabilityDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "service");
builder.Services.AddModuleSqlServer<SecurityAnalysisDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "security", pooled: true);
builder.Services.AddModuleSqlServer<PublishingDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "publishing", pooled: true);
builder.Services.AddModuleSqlServer<NetworkDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "network", pooled: true);
builder.Services.AddModuleSqlServer<SchedulingDbContext>(_ => SchedulingRegistration.Connection(builder.Configuration), "scheduling");
builder.Services.AddModuleSqlServer<OperationsDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "operations");
builder.Services.AddModuleSqlServer<LogAdmissionDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "logquota");
builder.Services.AddScoped<PolicyAdministration>();
builder.Services.AddScoped<TracePolicyAdministration>();
builder.Services.AddScoped<Vigiloom.Web.LogQuota.LogQuotaSettings>();
builder.Services.AddScoped<TraceQuotaSettings>();
builder.Services.AddHttpClient<Vigiloom.Web.LogQuota.TraceQuotaAccess>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] ?? "http://localhost").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IOperationAuditWriter<OperationAudit>, OperationAuditWriter>();
builder.Services.AddScoped<IdentityAuditRetention>();
builder.Services.AddScoped<IAuditRetentionSource>(provider =>
    string.IsNullOrWhiteSpace(provider.GetRequiredService<IConfiguration>()["Services:Identity:Url"])
        ? provider.GetRequiredService<IdentityAuditRetention>()
        : provider.GetRequiredService<RemoteIdentityAuditRetention>());
builder.Services.AddHttpClient<RemoteIdentityAuditRetention>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddServiceDiscovery();
builder.Services.AddScoped<IAuditRetentionSource, DeviceAuditRetention>();
builder.Services.AddScoped<IAuditRetentionSource, DashboardAuditRetention>();
builder.Services.AddScoped<IAuditRetentionSource, AgentAuditRetention>();
builder.Services.AddScoped<IAuditRetentionSource, SecurityAuditRetention>();
builder.Services.AddScoped<IHostLogRetention, HostLogRetention>();
builder.Services.AddScoped<ISecurityDetectionRetention, SecurityDetectionRetention>();
builder.Services.AddScoped<OperationAuditQueryService>();
builder.Services.AddScoped<Vigiloom.Modules.AlertingOperations.Application.Retention.AuditRetentionSettings>();
builder.Services.AddScoped<IdentityAuditFeed>();
builder.Services.AddScoped<DeviceAuditFeed>();
builder.Services.AddScoped<AgentAuditFeed>();
builder.Services.AddHttpClient<RemoteHostAuditFeed>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<InventoryAuditFeed>();
builder.Services.AddScoped<AuditFeedReader>();
builder.Services.AddScoped<IdentityAuditReader>();
builder.Services.AddVigiloomScheduling(builder.Configuration);
builder.Services.AddVigiloomDataProtection(builder.Configuration);
builder.Services.AddScoped<Vigiloom.Modules.AlertingOperations.Application.Line.LineChannel>();
builder.Services.AddHttpClient<Vigiloom.Modules.AlertingOperations.Application.Line.LinePushClient>();
builder.Services.AddScoped<Vigiloom.Infrastructure.Scheduling.Outbox.IOutboxHandler, Vigiloom.Modules.AlertingOperations.Application.Line.LineDeliveryHandler>();
builder.Services.AddScoped<Vigiloom.Modules.AlertingOperations.Application.Line.LineDeliveryQueue>();
builder.Services.AddScoped<Vigiloom.Modules.AlertingOperations.Application.Line.LineDeliveryStatus>();
builder.Services.AddScoped<Vigiloom.Web.Operations.LineManagement>();
builder.Services.AddHttpClient<Vigiloom.Web.Operations.RemoteLine>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Operations:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddVigiloomIdentity();
builder.Services.AddScoped<AccountLogin>();
builder.Services.AddScoped<CredentialManagement>();
builder.Services.AddScoped<AccountManagement>();
builder.Services.AddScoped<AccountPasswordOperations>();
builder.Services.AddScoped<TwoFactorSettings>();
builder.Services.AddScoped<LocalApiCredentialValidation>();
builder.Services.AddVigiloomIdentityCredentialClient();
builder.Services.AddVigiloomIdentityCookieClient();
builder.Services.AddHttpClient<RemoteWorkspaceIdentity>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddServiceDiscovery();
builder.Services.AddHttpClient<RemoteCookieValidator>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddServiceDiscovery();
builder.Services.AddHttpClient<RemotePasswordLogin>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddServiceDiscovery();
builder.Services.AddHttpClient<RemoteCredentialManagement>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddServiceDiscovery();
builder.Services.AddHttpClient<RemoteAccountManagement>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddServiceDiscovery();
builder.Services.AddHttpClient<RemoteAccountPasswordOperations>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddServiceDiscovery();
builder.Services.AddHttpClient<RemoteTwoFactorSettings>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddServiceDiscovery();
builder.Services.AddHttpClient<RemoteIdentityAuditReader>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddServiceDiscovery();
builder.Services.AddScoped<IApiCredentialValidation>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"])
        ? services.GetRequiredService<LocalApiCredentialValidation>()
        : services.GetRequiredService<RemoteApiCredentialValidation>());
builder.Services.AddMudServices();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DeviceManagement>();
builder.Services.AddHttpClient<Vigiloom.Web.Devices.RemoteDeviceManagement>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IDeviceManagement>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<DeviceManagement>()
        : services.GetRequiredService<Vigiloom.Web.Devices.RemoteDeviceManagement>());
builder.Services.AddScoped<GroupManagement>();
builder.Services.AddHttpClient<Vigiloom.Web.Groups.RemoteGroupManagement>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IGroupManagement>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<GroupManagement>()
        : services.GetRequiredService<Vigiloom.Web.Groups.RemoteGroupManagement>());
builder.Services.AddScoped<Vigiloom.Web.Events.EventTimelineReader>();
builder.Services.AddScoped<Vigiloom.Web.Operations.AuditQueryReader>();
builder.Services.AddHttpClient<Vigiloom.Web.Operations.RemoteAuditQuery>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Operations:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<Vigiloom.Web.Events.RemoteTimelineReader>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Operations:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Web.Events.EventTimelineAudit>();
builder.Services.AddScoped<ServiceMembership>();
builder.Services.AddHttpClient<RemoteServiceMembership>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IServiceMembership>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<ServiceMembership>()
        : services.GetRequiredService<RemoteServiceMembership>());
builder.Services.AddScoped<Vigiloom.Web.ServiceDetails.DeploymentLinks>();
builder.Services.AddHttpClient<Vigiloom.Web.ServiceDetails.RemoteDeploymentLinks>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Web.ServiceDetails.IDeploymentLinks>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<Vigiloom.Web.ServiceDetails.DeploymentLinks>()
        : services.GetRequiredService<Vigiloom.Web.ServiceDetails.RemoteDeploymentLinks>());
builder.Services.AddScoped<IDevicePingSource, DevicePingSource>();
builder.Services.AddScoped<IDeviceAgentSource, DeviceAgentSource>();
builder.Services.AddScoped<AgentIdentity>();
builder.Services.AddHttpClient<Vigiloom.Web.AgentVersions.RemoteAgentRegistration>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IAgentRegistration>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<AgentIdentity>()
        : services.GetRequiredService<Vigiloom.Web.AgentVersions.RemoteAgentRegistration>());
builder.Services.AddScoped<IMachineAgentIdentity>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<AgentIdentity>()
        : services.GetRequiredService<Vigiloom.Web.AgentVersions.RemoteAgentRegistration>());
builder.Services.AddScoped<CandidateDiscovery>();
builder.Services.AddScoped<ICandidateObserver>(services => services.GetRequiredService<CandidateDiscovery>());
builder.Services.AddScoped<TelemetryExports>();
builder.Services.AddScoped<TelemetryHistory>();
builder.Services.AddScoped<TelemetryRetentionSettings>();
builder.Services.AddScoped<Vigiloom.Web.ServiceDetails.LegacyHostLogCleanup>();
builder.Services.AddHttpClient<Vigiloom.Web.ServiceDetails.TelemetryRetentionAccess>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] ?? "http://localhost").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<ServiceEvidenceReader>();
builder.Services.AddScoped<ServiceHttpMetricsReader>();
builder.Services.AddScoped<ServiceDetailsReader>();
builder.Services.AddScoped<ReceptionOverview>();
builder.Services.AddScoped<ReceptionRejectionReader>();
builder.Services.AddHttpClient<Vigiloom.Web.Services.ReceptionManagement>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] ?? "http://localhost").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<CollectorObservations>();
builder.Services.AddScoped<CollectorCounters>();
builder.Services.AddScoped<CollectorAdministration>();
builder.Services.AddHttpClient<Vigiloom.Web.Services.CollectorManagement>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] ?? "http://localhost").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddSingleton<OtlpConcurrency>();
builder.Services.AddSingleton<MetricRollupPayloadGate>();
builder.Services.AddSingleton(provider => new ReceptionRejectionDiagnostics(
    () => provider.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>(),
    provider.GetRequiredService<ILogger<ReceptionRejectionDiagnostics>>()));
builder.Services.AddHostedService(provider => provider.GetRequiredService<ReceptionRejectionDiagnostics>());
builder.Services.AddScoped<ServiceApproval>();
builder.Services.AddScoped<Vigiloom.Web.Services.LocalCandidateAdministration>();
builder.Services.AddHttpClient<Vigiloom.Web.Services.RemoteCandidateAdministration>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Web.Services.ICandidateAdministration>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<Vigiloom.Web.Services.LocalCandidateAdministration>()
        : services.GetRequiredService<Vigiloom.Web.Services.RemoteCandidateAdministration>());
builder.Services.AddScoped<ServiceInstanceAdministration>();
builder.Services.AddScoped<Vigiloom.Modules.ServiceObservability.Application.Health.HealthTargetAdministration>();
builder.Services.AddScoped<Vigiloom.Modules.ServiceObservability.Application.Health.HealthNetworkPolicyAdministration>();
builder.Services.AddScoped<Vigiloom.Modules.ServiceObservability.Application.Health.HealthObservationReader>();
builder.Services.AddHttpClient<Vigiloom.Web.Services.HealthAdmin>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] ?? "http://localhost").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Modules.ServiceObservability.Application.Health.HealthStatusReader>();
builder.Services.AddScoped<Vigiloom.Modules.ServiceObservability.Application.Health.HealthHistoryReader>();
builder.Services.AddScoped<InstancePolicyAdministration>();
builder.Services.AddHttpClient<Vigiloom.Web.Services.InstanceManagement>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] ?? "http://localhost").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<ServiceCollectionAdministration>();
builder.Services.AddScoped<ApplicationStateProjection>();
builder.Services.AddScoped<Vigiloom.Web.Services.LocalApplicationManagement>();
builder.Services.AddHttpClient<Vigiloom.Web.Services.RemoteApplicationManagement>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Web.Services.IApplicationManagement>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<Vigiloom.Web.Services.LocalApplicationManagement>()
        : services.GetRequiredService<Vigiloom.Web.Services.RemoteApplicationManagement>());
builder.Services.AddScoped<SourceCredentials>();
builder.Services.AddScoped<SourceAdministration>();
builder.Services.AddHttpClient<Vigiloom.Web.Services.SourceManagement>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] ?? "http://localhost").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<AgentMonitoring>();
builder.Services.AddHttpClient<Vigiloom.Web.HostMonitoring.RemoteAgentStatus>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Contracts.Devices.IAgentStatusReader>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<AgentMonitoring>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentStatus>());
builder.Services.AddScoped<Vigiloom.Contracts.Devices.IAgentOverviewReader>(services => services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentStatus>());
builder.Services.AddScoped<HostMetricHistory>();
builder.Services.AddHttpClient<Vigiloom.Web.HostMonitoring.RemoteHostMetrics>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IHostMetricHistory>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<HostMetricHistory>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteHostMetrics>());
builder.Services.AddScoped<HostMetricRetentionSettings>();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IHostMetricRetentionSettings>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<HostMetricRetentionSettings>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteHostMetrics>());
builder.Services.AddScoped<HostMetricThresholds>();
builder.Services.AddHttpClient<Vigiloom.Web.HostMonitoring.RemoteHostThresholds>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IHostMetricThresholds>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<HostMetricThresholds>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteHostThresholds>());
builder.Services.AddScoped<AgentConfiguration>();
builder.Services.AddHttpClient<Vigiloom.Web.HostMonitoring.RemoteAgentSettings>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IAgentSettings>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<AgentConfiguration>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentSettings>());
builder.Services.AddScoped<AgentQueueCapacityConfiguration>();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IAgentQueueCapacitySettings>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<AgentQueueCapacityConfiguration>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentSettings>());
builder.Services.AddScoped<AgentMetricConfiguration>();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IAgentMetricSettings>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<AgentMetricConfiguration>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentSettings>());
builder.Services.AddScoped<HostLogConfiguration>();
builder.Services.AddHttpClient<Vigiloom.Web.HostMonitoring.RemoteHostLogSettings>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IHostLogConfiguration>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<HostLogConfiguration>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteHostLogSettings>());
builder.Services.AddScoped<AgentPackageQuery>();
builder.Services.AddHttpClient<Vigiloom.Web.AgentPackages.RemoteAgentPackageQuery>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IAgentPackageQuery>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<AgentPackageQuery>()
        : services.GetRequiredService<Vigiloom.Web.AgentPackages.RemoteAgentPackageQuery>());
builder.Services.AddScoped<AgentVersionAssignments>();
builder.Services.AddHttpClient<Vigiloom.Web.AgentVersions.RemoteAgentAssignments>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IAgentAssignments>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<AgentVersionAssignments>()
        : services.GetRequiredService<Vigiloom.Web.AgentVersions.RemoteAgentAssignments>());
builder.Services.AddScoped<IAgentVersionDelivery>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<AgentVersionAssignments>()
        : services.GetRequiredService<Vigiloom.Web.AgentVersions.RemoteAgentAssignments>());
builder.Services.AddScoped<Vigiloom.Modules.AgentManagement.Application.YaraScans>();
builder.Services.AddScoped<Vigiloom.Web.AgentVersions.YaraScanAccess>();
builder.Services.AddHttpClient("security", (services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Security:Url"] ?? "http://vigiloom-security";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<AgentPackageAccess>();
builder.Services.AddHttpClient<Vigiloom.Web.AgentPackages.RemoteAgentUpdateAccess>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IAgentUpdateAccess>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<AgentPackageAccess>()
        : services.GetRequiredService<Vigiloom.Web.AgentPackages.RemoteAgentUpdateAccess>());
builder.Services.AddScoped<AgentUpdateResults>();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IAgentVersionClient, Vigiloom.Web.AgentVersions.ServerAgentVersionClient>();
builder.Services.AddScoped<PackageLibrary>();
builder.Services.AddHttpClient<Vigiloom.Web.AgentPackages.RemoteAgentPackageLibrary>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IAgentPackageLibrary>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"])
        ? services.GetRequiredService<PackageLibrary>()
        : services.GetRequiredService<Vigiloom.Web.AgentPackages.RemoteAgentPackageLibrary>());
builder.Services.AddHttpClient<GitHubAgentReleases>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Vigiloom");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<IPackageLibraryTransport>(services =>
{
    var directory = builder.Configuration["Vigiloom:AgentPackages:CachePath"];
    if (string.IsNullOrWhiteSpace(directory))
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData)) throw new InvalidOperationException("Set Vigiloom:AgentPackages:CachePath to a private node-local directory.");
        directory = Path.Combine(localData, "Vigiloom", "agent-packages");
    }
    return new PackageLibraryTransport(directory);
});
builder.Services.AddScoped<HostLogMonitoring>();
builder.Services.AddScoped<IHostLogMonitoring>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<HostLogMonitoring>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentStatus>());
builder.Services.AddHttpClient<Vigiloom.Web.LogQuota.LogQuotaReader>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Operations:Url"] ?? "http://vigiloom-operations").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<HostLogHistory>();
builder.Services.AddHttpClient<Vigiloom.Web.HostMonitoring.HostLogEvidenceReader>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Operations:Url"] ?? "http://vigiloom-operations").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<Vigiloom.Web.HostMonitoring.RemoteHostLogs>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Operations:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IHostLogHistory>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Operations:Url"]) ? services.GetRequiredService<HostLogHistory>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteHostLogs>());
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IHostLogReader, HostLogReader>();
builder.Services.AddScoped<AgentReplayPolicyService>();
builder.Services.AddScoped<AgentInboxSettings>();
builder.Services.AddHttpClient<Vigiloom.Web.HostMonitoring.RemoteInboxSettings>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IAgentInboxSettings>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<AgentInboxSettings>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteInboxSettings>());
builder.Services.AddScoped<IAgentReplayPolicy>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<AgentReplayPolicyService>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteInboxSettings>());
builder.Services.AddScoped<AgentQueueCapacityMonitoring>();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IAgentQueueCapacityStatusReader>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<AgentQueueCapacityMonitoring>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentStatus>());
builder.Services.AddScoped<AgentSettingsMonitoring>();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IAgentSettingsStatusReader>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<AgentSettingsMonitoring>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentStatus>());
builder.Services.AddScoped<AgentBatchReceiver>();
builder.Services.AddScoped<IInventoryStageWriter, InventoryStageWriter>();
builder.Services.AddScoped<IInventoryStatusReader, InventoryStatusReader>();
builder.Services.AddScoped<IInventorySettingsReader, InventorySettingsReader>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Integrity.IntegrityConfiguration>();
builder.Services.AddScoped<Vigiloom.Web.AgentVersions.IntegritySettingsAccess>();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.Integrity.IIntegritySettingsReader, Vigiloom.Modules.SecurityAnalysis.Application.Integrity.IntegritySettingsReader>();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.Integrity.IIntegrityStageWriter, Vigiloom.Modules.SecurityAnalysis.Application.Integrity.IntegrityStageWriter>();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.Baseline.IBaselineWriter, Vigiloom.Modules.SecurityAnalysis.Application.Baseline.BaselineWriter>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Baseline.BaselineQuery>();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.Integrity.IIntegrityStatusReader, Vigiloom.Modules.SecurityAnalysis.Application.Integrity.IntegrityStatusReader>();
builder.Services.AddScoped<AgentIntegritySettings>();
builder.Services.AddScoped<AgentIntegrityStatus>();
builder.Services.AddScoped<InventoryConfiguration>();
builder.Services.AddScoped<Vigiloom.Web.AgentVersions.InventorySettingsAccess>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.IntelligenceStatus>();
builder.Services.AddScoped<Vigiloom.Contracts.Intelligence.IKevCatalog, Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.KevCatalog>();
builder.Services.AddScoped<Vigiloom.Contracts.Intelligence.IAdvisoryCatalog, Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories.AdvisoryCatalog>();
builder.Services.AddScoped<Vigiloom.Contracts.Intelligence.ISigmaCatalog, Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma.SigmaCatalog>();
builder.Services.AddHostDetection();
builder.Services.AddScoped<Vigiloom.Modules.AlertingOperations.Application.NotificationReader>();
builder.Services.AddScoped<Vigiloom.Contracts.Notifications.INotificationReader, Vigiloom.Web.Operations.OperationsNotificationReader>();
builder.Services.AddHttpClient<Vigiloom.Web.Operations.RemoteNotificationReader>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Operations:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Contracts.Intelligence.IBaselineSource, Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines.BaselineSource>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.IntelligencePolicy>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.IntelligencePublication>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories.AdvisoryRecovery>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories.AdvisoryCredentials>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.IntelligenceRevocation>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.KevOfflineImport>();
builder.Services.AddScoped<AgentInventorySettings>();
builder.Services.AddScoped<AgentInventorySettingsMonitoring>(services => new(
    services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>(), services.GetRequiredService<IDeviceAgentSource>(),
    services.GetRequiredService<IInventorySettingsReader>(), services.GetRequiredService<TimeProvider>(),
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? null : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentStatus>()));
builder.Services.AddScoped<AgentInventoryStatus>();
builder.Services.AddScoped<DeviceInventoryAgent>();
builder.Services.AddScoped<AgentOwnerResolver>();
builder.Services.AddHttpClient<Vigiloom.Web.AgentVersions.RemoteAgentOwnership>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IDeviceInventoryAgent>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]) ? services.GetRequiredService<DeviceInventoryAgent>() : services.GetRequiredService<Vigiloom.Web.AgentVersions.RemoteAgentOwnership>());
builder.Services.AddScoped<IAgentOwnerResolver>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]) ? services.GetRequiredService<AgentOwnerResolver>() : services.GetRequiredService<Vigiloom.Web.AgentVersions.RemoteAgentOwnership>());
builder.Services.AddScoped<IAgentBaselineSyncReader>(services => new AgentBaselineSyncReader(
    services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>(), services.GetRequiredService<IDeviceAgentSource>(),
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? null : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentStatus>()));
builder.Services.AddScoped<IBaselineExceptionReader, Vigiloom.Modules.AlertingOperations.Application.BaselineExceptionReader>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Baseline.BaselineExceptions>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines.CustomBaselineStage>();
builder.Services.AddScoped<Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities.VulnerabilityMonitoring>();
builder.Services.AddScoped<InventoryQuery>();
builder.Services.AddScoped<Vigiloom.Web.HostMonitoring.InventoryAccess>();
builder.Services.AddScoped<IntegrityQuery>();
builder.Services.AddScoped<IntegrityChangeQuery>();
builder.Services.AddScoped<AgentIntegritySettingsMonitoring>(services => new(
    services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>(), services.GetRequiredService<IDeviceAgentSource>(),
    services.GetRequiredService<Vigiloom.Contracts.Agent.Integrity.IIntegritySettingsReader>(), services.GetRequiredService<TimeProvider>(),
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? null : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentStatus>()));
builder.Services.AddScoped<InventoryChangeQuery>();
builder.Services.AddScoped<AgentReceiverMonitoring>();
builder.Services.AddHttpClient<Vigiloom.Web.HostMonitoring.RemoteAgentReceiver>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IAgentReceiverMonitoring>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]) ? services.GetRequiredService<AgentReceiverMonitoring>() : services.GetRequiredService<Vigiloom.Web.HostMonitoring.RemoteAgentReceiver>());
builder.Services.AddScoped<WorkerDiagnosticsStore>();
builder.Services.AddScoped<PlatformStatus>();
builder.Services.AddScoped<Vigiloom.Web.Diagnostics.JobOverview>();
builder.Services.AddHttpClient<Vigiloom.Web.Diagnostics.RemoteJobOverview>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Operations:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddSingleton<AgentTransportConcurrency>();
builder.Services.AddScoped<PingSettings>();
builder.Services.AddScoped<PingRetentionSettings>();
builder.Services.AddHttpClient<Vigiloom.Web.Network.RemotePingAdministration>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Network:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Contracts.Network.IPingSettings>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Network:Url"]) ? services.GetRequiredService<PingSettings>() : services.GetRequiredService<Vigiloom.Web.Network.RemotePingAdministration>());
builder.Services.AddScoped<Vigiloom.Contracts.Network.IPingRetentionSettings>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Network:Url"]) ? services.GetRequiredService<PingRetentionSettings>() : services.GetRequiredService<Vigiloom.Web.Network.RemotePingAdministration>());
builder.Services.AddScoped<PingStore>();
builder.Services.AddHttpClient<Vigiloom.Web.Network.RemotePingStatusReader>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Network:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Contracts.Network.IPingStatusReader>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Network:Url"])
        ? services.GetRequiredService<PingStore>() : services.GetRequiredService<Vigiloom.Web.Network.RemotePingStatusReader>());
builder.Services.AddScoped<Vigiloom.Modules.NetworkMonitoring.Application.PingHistory.PingHistoryReader>();
builder.Services.AddScoped<Vigiloom.Contracts.Network.IPingHistoryClient, Vigiloom.Web.Network.ServerPingHistoryClient>();
builder.Services.AddScoped<SpeedSettings>();
builder.Services.AddScoped<SpeedRetentionSettings>();
builder.Services.AddScoped<SpeedQueue>();
builder.Services.AddScoped<SpeedHistory>();
builder.Services.AddScoped<ISpeedHistoryClient, ServerSpeedHistoryClient>();
builder.Services.AddHttpClient<Vigiloom.Web.SpeedTests.RemoteSpeedAdministration>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Network:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Contracts.SpeedTests.ISpeedSettings>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Network:Url"]) ? services.GetRequiredService<SpeedSettings>() : services.GetRequiredService<Vigiloom.Web.SpeedTests.RemoteSpeedAdministration>());
builder.Services.AddScoped<Vigiloom.Contracts.SpeedTests.ISpeedRetentionSettings>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Network:Url"]) ? services.GetRequiredService<SpeedRetentionSettings>() : services.GetRequiredService<Vigiloom.Web.SpeedTests.RemoteSpeedAdministration>());
builder.Services.AddScoped<Vigiloom.Contracts.SpeedTests.ISpeedCancellation>(services =>
    string.IsNullOrWhiteSpace(services.GetRequiredService<IConfiguration>()["Services:Network:Url"]) ? services.GetRequiredService<SpeedQueue>() : services.GetRequiredService<Vigiloom.Web.SpeedTests.RemoteSpeedAdministration>());
builder.Services.AddHttpClient<Vigiloom.Web.Network.RemotePingHistory>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Network:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<Vigiloom.Web.SpeedTests.RemoteSpeedHistory>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Network:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Contracts.Agent.IHostMonitoringClient, Vigiloom.Web.HostMonitoring.ServerHostMonitoringClient>();
builder.Services.AddScoped<Vigiloom.Contracts.Vulnerabilities.IVulnerabilityClient, Vigiloom.Web.HostMonitoring.ServerVulnerabilityClient>();
builder.Services.AddScoped<Vigiloom.Contracts.Services.IServiceDetailsClient, Vigiloom.Web.ServiceDetails.ServerServiceDetailsClient>();
builder.Services.AddHttpClient<Vigiloom.Web.ServiceDetails.ServiceQueries>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] ?? "http://localhost").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<WorkspaceIdentityService>();
builder.Services.AddScoped<IWorkspaceIdentityClient, ServerWorkspaceIdentityClient>();
builder.Services.AddScoped<Vigiloom.Web.Workspace.WorkspaceOverviewReader>();
builder.Services.AddScoped<IWorkspaceOverviewClient, Vigiloom.Web.Workspace.ServerWorkspaceOverviewClient>();
builder.Services.AddScoped<Vigiloom.Web.Publishing.PublicationAdministration>();
builder.Services.AddScoped<Vigiloom.Web.Publishing.TemplateReader>();
builder.Services.AddScoped<Vigiloom.Web.Publishing.TemplateAdministration>();
builder.Services.AddScoped<Vigiloom.Web.Publishing.TemplateUpdateWorkflow>();
builder.Services.AddScoped<Vigiloom.Web.Publishing.TemplateRetirementWorkflow>();
builder.Services.AddScoped<Vigiloom.Web.Publishing.PublicReader>();
builder.Services.AddHttpClient<Vigiloom.Web.Publishing.RemotePublicationManagement>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<Vigiloom.Web.Publishing.RemotePublicReader>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]
        ?? throw new InvalidOperationException("Services:Resources:Url is required when using remote publication or the network service.")).TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<Vigiloom.Web.Publishing.RemoteNetworkPublicClient>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Network:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Web.Publishing.PublicServiceHistoryReader>();
builder.Services.AddHttpClient<Vigiloom.Web.Publishing.PublicObservationClient>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] ?? "http://localhost").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<Vigiloom.Web.Publishing.PublicDeviceHistoryReader>();
builder.Services.AddScoped<Vigiloom.Web.Publishing.DeviceHistoryPreviewReader>();
builder.Services.AddSingleton<Vigiloom.Web.Publishing.PublicHistoryAdmission>();
builder.Services.AddSingleton<Vigiloom.Web.Publishing.PublicStreamAdmission>();
builder.Services.AddScoped<Vigiloom.Web.Publishing.PublicationPreviewReader>();
builder.Services.AddScoped<Vigiloom.Contracts.Publishing.IPublicationClient, Vigiloom.Web.Publishing.ServerPublicationClient>();
builder.Services.AddScoped<Vigiloom.Contracts.Publishing.IPublicStatusClient, Vigiloom.Web.Publishing.ServerPublicStatusClient>();
builder.Services.AddSingleton<Vigiloom.Web.Publishing.PublicStatusAdmission>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.Headers.RetryAfter = "60";
        if (context.HttpContext.Request.Path.StartsWithSegments("/api"))
        {
            context.HttpContext.Response.ContentType = "application/problem+json";
            await context.HttpContext.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too Many Requests",
                Extensions = { ["code"] = "too_many_requests" }
            }, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        }
        else await context.HttpContext.Response.WriteAsync("請求過於頻繁，請稍後再試。", cancellationToken);
    };
    options.AddPolicy("identity-setup", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("identity-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Clamp(builder.Configuration.GetValue("Vigiloom:Authentication:RequestsPerMinute", 30), 1, 1000),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("api-client", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Clamp(builder.Configuration.GetValue("Vigiloom:Authentication:ApiRequestsPerMinute", 300), 1, 10000),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("service-discovery", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Clamp(builder.Configuration.GetValue("Vigiloom:ServiceDiscovery:RequestsPerMinute", 6000), 1, 100000),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
    options.AddPolicy("agent-transport", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "unverified", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Clamp(builder.Configuration.GetValue("Vigiloom:Agent:Transport:RequestsPerMinute", 120), 1, 10000),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }));
});
builder.Services.AddOpenApi(options => options.AddDocumentTransformer<ApiOpenApi>());
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
    {
        400 => "bad_request", 401 => "unauthorized", 403 => "forbidden", 404 => "not_found",
        409 => "conflict", 410 => "expired", 413 => "payload_too_large", 429 => "too_many_requests",
        503 => "service_unavailable", _ => "request_failed"
    });
});
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OperationsDbContext>(name: "database", tags: ["platform"]);

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(app.Configuration["Services:Network:Url"]) && string.IsNullOrWhiteSpace(app.Configuration["Services:Resources:Url"]))
    throw new InvalidOperationException("Services:Resources:Url is required when Services:Network:Url is configured.");

var forwardedHeaders = TrustedProxyForwarding.Create(builder.Configuration);
if (forwardedHeaders is not null) app.UseForwardedHeaders(forwardedHeaders);

// Anonymous status HTML may contain published information and must not survive revocation in caches.
app.Use(async (context, next) =>
{
    if (context.Request.Path == "/" || context.Request.Path == "/status" || context.Request.Path == "/status/")
        context.Response.Headers.CacheControl = "no-store";
    await next();
});

// API errors must not be rewritten as a Blazor HTML page.
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api"), api =>
{
    api.Use(async (context, next) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        await next();
    });
    api.UseExceptionHandler();
    api.UseWhen(context => !context.Request.Path.StartsWithSegments(OtlpHttpEndpoints.Prefix), other => other.UseStatusCodePages());
    api.UseWhen(context => context.Request.Path.StartsWithSegments(OtlpHttpEndpoints.Prefix), otlp =>
    {
        otlp.Use(async (context, next) =>
        {
            try { await next(); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception) when (!context.Response.HasStarted)
            {
                await OtlpResponse.Failure(StatusCodes.Status503ServiceUnavailable, "Telemetry receiver is unavailable; retry later.").ExecuteAsync(context);
                return;
            }
            if (!context.Response.HasStarted && context.Response.StatusCode >= 400 && context.Response.ContentLength is null)
                await OtlpResponse.Failure(context.Response.StatusCode, "Telemetry request was rejected.").ExecuteAsync(context);
        });
    });
});

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.MapOpenApi();
}
else
{
    app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), web =>
        web.UseExceptionHandler("/Error", createScopeForErrors: true));
    app.UseHsts();
}

app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/admin/jobs") &&
    !context.Request.Path.Equals("/settings/agent-packages/trust", StringComparison.OrdinalIgnoreCase) &&
    !(context.Request.Path.StartsWithSegments("/settings/intelligence") && context.Request.Path.Value?.EndsWith("/offline-import", StringComparison.OrdinalIgnoreCase) == true), web =>
    web.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseRouting();
app.UseMiddleware<OtlpTransportGuard>();
app.UseMiddleware<AgentTransportGuard>();
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/workspace") || context.Request.Path.StartsWithSegments("/settings") || context.Request.Path.StartsWithSegments("/devices") || context.Request.Path.StartsWithSegments("/groups") || context.Request.Path.StartsWithSegments("/speed-tests") || context.Request.Path.StartsWithSegments("/api/v1/public") || context.Request.Path.StartsWithSegments("/api/v1/admin/publications"))
        context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseAuthorization();
app.UseAntiforgery();

// Resolve storage only for an authorized Dashboard request; liveness and public assets do not depend on SQL.
((IApplicationBuilder)app).Map("/admin/jobs", dashboard =>
{
    dashboard.UseMiddleware<DashboardGuard>();
    dashboard.Run(async context =>
    {
        var options = new DashboardOptions { AppPath = "/settings/jobs", Authorization = [new AdministratorDashboardFilter()] };
        var middleware = new AspNetCoreDashboardMiddleware(_ => Task.CompletedTask,
            context.RequestServices.GetRequiredService<JobStorage>(), options,
            context.RequestServices.GetRequiredService<Hangfire.Dashboard.RouteCollection>(), finalizeWhenNotFound: true);
        await middleware.Invoke(context);
    });
});

// Liveness reports the Web process, not whether dependencies are reachable.
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();
// This endpoint is for trusted internal service traffic and must not be exposed by the gateway.
app.MapGet("/internal/telemetry/source/identity", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { SourceId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value });
}).RequireAuthorization("ServiceSource");
app.MapAccountEndpoints();
app.MapDeviceEndpoints();
app.MapPublicationEndpoints();
app.MapGroupEndpoints();
app.MapAgentEndpoints();
app.MapInventoryEndpoints();
app.MapVulnerabilityEndpoints();
app.MapIntegrityMonitoringEndpoints();
app.MapAgentPackageEndpoints();
app.MapAgentUpdateDownloadEndpoints();
app.MapAgentPackageUiEndpoints();
app.MapHostMetricEndpoints();
app.MapHostMetricThresholdEndpoints();
app.MapAgentConfigurationEndpoints();
app.MapAgentVersionEndpoints();
app.MapYaraScanEndpoints();
app.MapAgentQueueCapacityEndpoints();
app.MapAgentMetricSettingsEndpoints();
app.MapInventorySettingsEndpoints();
app.MapBaselineSettingsEndpoints();
app.MapIntelligenceEndpoints();
app.MapSigmaDetectionEndpoints();
app.MapIocDetectionEndpoints();
app.MapEventTimelineEndpoints();
app.MapIntelligenceOfflineUiEndpoints();
app.MapIntegrityScopeEndpoints();
app.MapHostLogSettingsEndpoints();
app.MapHostLogEndpoints();
app.MapAgentReplayPolicyEndpoints();
app.MapAgentInboxSettingsEndpoints();
app.MapAgentBatchEndpoints();
app.MapServiceDiscoveryEndpoints();
app.MapLineWebhookEndpoints();
app.MapLineSettingsEndpoints();
app.MapGet("/internal/v1/observation-sources/{id:guid}/active", async (Guid id, IDbContextFactory<ServiceObservabilityDbContext> contexts, CancellationToken token) =>
{
    await using var db = await contexts.CreateDbContextAsync(token);
    var source = await db.Sources.AsNoTracking().Where(source => source.Id == id)
        .Select(source => new Vigiloom.Contracts.Resources.ApplicationSourceStatus(source.RevokedAt == null, source.Name)).SingleOrDefaultAsync(token);
    return Results.Ok(source ?? new Vigiloom.Contracts.Resources.ApplicationSourceStatus(false, null));
});
app.MapPost("/internal/v1/agent-package-usage", async (Vigiloom.Contracts.Agent.AgentPackageUsageRequest request,
    IDbContextFactory<AgentManagementDbContext> contexts, CancellationToken token) =>
{
    await using var db = await contexts.CreateDbContextAsync(token);
    var inUse = await db.Agents.AsNoTracking().AnyAsync(a => a.RevokedAt == null && a.RuntimeVersion == request.Version &&
        a.RuntimeIdentifier == request.RuntimeIdentifier, token);
    return Results.Ok(new Vigiloom.Contracts.Agent.AgentPackageUsageResponse(inUse));
});
app.MapGet("/internal/v1/agents/{id:guid}/update-status", async (Guid id,
    IDbContextFactory<AgentManagementDbContext> contexts, CancellationToken token) =>
{
    await using var db = await contexts.CreateDbContextAsync(token);
    var agent = await db.Agents.AsNoTracking().Where(a => a.Id == id && a.RevokedAt == null)
        .Select(a => new { a.RuntimeVersion, a.RuntimeIdentifier, a.RuntimeReportedAt }).SingleOrDefaultAsync(token);
    if (agent is null) return Results.NotFound();
    var latest = await AgentVersionObservations.LatestAsync(db, id, token);
    return Results.Ok(new Vigiloom.Contracts.Agent.AgentVersionObservation(agent.RuntimeVersion, agent.RuntimeIdentifier, agent.RuntimeReportedAt, latest));
});
app.MapOtlpHttpEndpoints();
app.MapTelemetryHistoryEndpoints();
app.MapTelemetryRetentionEndpoints();
app.MapPost("/internal/v1/legacy-host-logs/cleanup", async (Vigiloom.Contracts.Operations.ApplicationLogCleanupRequest input,
    Vigiloom.Web.ServiceDetails.LegacyHostLogCleanup cleanup, CancellationToken token) =>
    input.RetentionDays is < 1 or > 3650 ? Results.BadRequest() : Results.Ok(await cleanup.RunAsync(input.RetentionDays, token)));
app.MapServiceDetailsEndpoints();
app.MapServiceInstanceEndpoints();
app.MapHealthTargetEndpoints();
app.MapLogQuotaEndpoints();
app.MapPingEndpoints();
app.MapSpeedTestEndpoints();
app.MapOperationAuditEndpoints();
app.MapPlatformStatusEndpoints();
app.MapGet("/api/v1/workspace/identity", async (HttpContext context, WorkspaceIdentityService service, CancellationToken token) =>
    (await service.GetAsync(context.User, token)) is { } identity ? Results.Ok(identity) : Results.Unauthorized())
    .RequireAuthorization(AccessPolicies.MonitoringRead).Produces<WorkspaceIdentity>().ProducesProblem(401).ProducesProblem(403)
    .WithSummary("Get the current monitoring user's identity");
app.MapGet("/api/v1/workspace/overview", async (HttpContext context, Vigiloom.Web.Workspace.WorkspaceOverviewReader overview, CancellationToken token) =>
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await overview.ReadAsync(token));
    }).RequireAuthorization(AccessPolicies.MonitoringRead).RequireRateLimiting("api-client").Produces<WorkspaceOverview>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
    .WithSummary("Get the monitoring workspace overview");
app.MapGet("/api/v1/access", (HttpContext context) => TypedResults.Ok(new ApiClientAccessResponse(
    context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value,
    context.User.FindAll("scope").Select(claim => claim.Value).ToArray())))
    .RequireAuthorization("ApiClientRead").RequireRateLimiting("api-client")
    .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).WithSummary("Inspect the authenticated API client's scopes");
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(Vigiloom.Web.Client._Imports).Assembly);

app.Run();

// Exposes the entry point to WebApplicationFactory without executing an external server.
public partial class Program;

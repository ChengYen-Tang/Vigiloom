using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Vigiloom.Contracts.Agent;
using Vigiloom.HostMonitoring;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Application.HostMetrics;
using Hangfire;
using Hangfire.SqlServer;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Infrastructure.Telemetry;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;

var builder = WebApplication.CreateBuilder(args);
builder.AddVigiloomSelfTelemetry("Vigiloom.Host");
builder.Services.AddVigiloomDataProtection(builder.Configuration);
builder.Services.AddOptions<IdentityOptions>();
builder.Services.AddAntiforgery();
builder.Services.AddVigiloomIdentityCookieClient();
builder.Services.AddVigiloomIdentityCredentialClient();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddAuthentication().AddVigiloomApiCredentials();
builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options =>
{
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
    options.Events.OnValidatePrincipal = context => context.HttpContext.RequestServices.GetRequiredService<IdentityCookieValidator>().ValidateAsync(context);
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddAuthorization(options => options.AddVigiloomAccessPolicies());
builder.Services.AddModuleSqlServer<HostMonitoringDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("HostMonitoring")
        ?? throw new InvalidOperationException("ConnectionStrings:HostMonitoring is required."),
    "agent", pooled: true, migrationHistory: HostMonitoringDbContext.MigrationHistory);
builder.Services.AddServiceDiscovery();
builder.Services.AddHttpClient<IHostLogSink, OperationsLogSink>((services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Operations:Url"] ?? "http://vigiloom-operations";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<IHostLogIdentities, OperationsLogIdentities>((services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Operations:Url"] ?? "http://vigiloom-operations";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(5);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<IHostWorkRetentionDays, OperationsWorkRetentionDays>((services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Operations:Url"] ?? "http://vigiloom-operations";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(5);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<HostObservationReceiver>();
builder.Services.AddScoped<HostHeartbeat>();
builder.Services.AddScoped<HostAgentConfiguration>();
builder.Services.AddScoped<HostQueueCapacity>();
builder.Services.AddScoped<HostMetricConfiguration>();
builder.Services.AddScoped<HostMonitoringThresholds>();
builder.Services.AddScoped<HostAuditFeed>();
builder.Services.AddScoped<HostReceiverMonitoring>();
builder.Services.AddScoped<HostInboxSettings>();
builder.Services.AddScoped<HostReplayPolicy>();
builder.Services.AddScoped<HostMonitoringLogConfiguration>();
builder.Services.AddScoped<HostAgentMonitoring>();
builder.Services.AddScoped<HostMetricReader>();
builder.Services.AddScoped<HostOwnedMetricRetentionSettings>();
builder.Services.AddScoped<OutboxWriter>();
builder.Services.AddScoped<IOperationAuditWriter<OperationAudit>, TemporalAuditWriter>();
builder.Services.AddScoped<HostUpdateResults>();
builder.Services.AddScoped<HostInboxCleanup>();
builder.Services.AddScoped<HostLogIdentityCleanup>();
builder.Services.AddScoped<HostWorkRetention>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AgentHeartbeatLimits>();
if (builder.Configuration.GetValue("Hangfire:Enabled", true))
{
    builder.Services.AddHangfire((services, settings) => settings.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
        .UseSqlServerStorage(services.GetRequiredService<IConfiguration>().GetConnectionString("Scheduling")
            ?? throw new InvalidOperationException("ConnectionStrings:Scheduling is required."), SchedulingRegistration.StorageOptions(prepareSchema: false)));
    builder.Services.AddScoped<HostInboxRetentionJob>();
    builder.Services.AddScoped<HostMetricMaintenance>();
    builder.Services.AddHostedService<HostInboxRetentionSchedule>();
    builder.Services.AddHostedService<HostMetricMaintenanceSchedule>();
    builder.Services.AddHangfireServer(options => { options.Queues = ["host"]; options.IsLightweightServer = true; });
}
builder.Services.AddHttpClient("resources", (services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient("operations", (services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Operations:Url"] ?? "http://vigiloom-operations";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient("security", (services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Security:Url"] ?? "http://vigiloom-security";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<IRecordedAgentTargetReader, RecordedAgentTargets>((services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
var signalR = builder.Services.AddSignalR();
if (builder.Configuration.GetConnectionString("SignalR") is { Length: > 0 } backplane)
    signalR.AddStackExchangeRedis(backplane);
if (builder.Configuration.GetValue("Temporal:Enabled", true))
{
    builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233",
        builder.Configuration["Temporal:Namespace"] ?? "default");
    builder.Services.AddVigiloomTemporalWorker<AgentChangeHandler>(builder.Configuration["Work:AgentQueue"] ?? "vigiloom.agent.resources");
    builder.Services.AddScoped<IHostSecurityWorkWriter, HostSecurityWorkWriter>();
    builder.Services.AddScoped(provider => new OutboxDispatcher(provider.GetRequiredService<IConfiguration>().GetConnectionString("HostMonitoring")
        ?? throw new InvalidOperationException("ConnectionStrings:HostMonitoring is required."), provider.GetServices<IOutboxHandler>(),
        provider.GetRequiredService<ILogger<OutboxDispatcher>>()));
    builder.Services.AddHostedService<WorkDispatcher>();
}
var app = builder.Build();
var forwardedHeaders = TrustedProxyForwarding.Create(builder.Configuration);
if (forwardedHeaders is not null) app.UseForwardedHeaders(forwardedHeaders);
app.UseAuthentication();
app.UseAuthorization();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/v1/agent/channel") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/yara-scans") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/inventory-settings") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/inventory/status") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/integrity-settings") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/integrity-status") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/baseline-settings") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/batches") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/heartbeat") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/queue-session") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/queue-capacity") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/update-results") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/settings") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/metric-settings") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/log-settings"), agent => agent.UseMiddleware<AgentAccess>());
app.MapHub<AgentChannel>("/api/v1/agent/channel");
app.MapAgentSecuritySettingsEndpoints();
app.MapGet("/api/v1/agent/yara-scans/next", async (HttpContext context, IHttpClientFactory clients, CancellationToken token) =>
{
    context.Response.Headers.CacheControl = "private, no-store";
    var identity = (RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!;
    using var response = await clients.CreateClient("security").PostAsJsonAsync("internal/v1/yara-scans/next", identity, token);
    if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return Results.NoContent();
    response.EnsureSuccessStatusCode();
    var job = await response.Content.ReadFromJsonAsync<Vigiloom.Contracts.Agent.Security.YaraScanJob>(cancellationToken: token);
    return job is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable) : Results.Ok(job);
});
app.MapAgentBatchEndpoints();
app.MapHostAgentSettingsEndpoints();
app.MapHostQueueCapacityEndpoints();
app.MapHostMetricSettingsEndpoints();
app.MapHostThresholdEndpoints();
app.MapHostAuditEndpoints();
app.MapHostLogSettingsEndpoints();
app.MapHostInboxEndpoints();
app.MapGet("/internal/v1/agent-receiver/status", async (HostReceiverMonitoring receiver, CancellationToken token) =>
    Results.Ok(new AgentReceiverSnapshot(await receiver.LastCommittedBatchAsync(token))));
app.MapHostAgentStatusEndpoints();
app.MapHostMetricEndpoints();
app.MapHostMetricRetentionEndpoints();
app.MapPost("/api/v1/agent/heartbeat", AgentHeartbeatEndpoint.ReceiveAsync)
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(AgentHeartbeatEndpoint.MaximumBytes));
app.MapPost("/api/v1/agent/update-results", AgentUpdateResultEndpoint.ReceiveAsync)
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(AgentUpdateResultEndpoint.MaximumBytes));
app.MapGet("/internal/v1/agents/{id:guid}/update-status", async (Guid id, HostUpdateResults results, CancellationToken token) =>
    await results.GetAsync(id, token) is { } status ? Results.Ok(status) : Results.NotFound());
app.MapPost("/internal/v1/agent-package-usage", async (AgentPackageUsageRequest request, IDbContextFactory<HostMonitoringDbContext> contexts,
    IHttpClientFactory clients, CancellationToken token) =>
{
    await using var db = await contexts.CreateDbContextAsync(token);
    var ids = await db.AgentStatuses.AsNoTracking().Where(x => x.RuntimeVersion == request.Version && x.RuntimeIdentifier == request.RuntimeIdentifier)
        .Select(x => x.Id).ToArrayAsync(token);
    if (ids.Length == 0) return Results.Ok(new AgentPackageUsageResponse(false));
    using var response = await clients.CreateClient("resources").PostAsJsonAsync("internal/v1/agents/active", ids, token);
    response.EnsureSuccessStatusCode();
    var active = await response.Content.ReadFromJsonAsync<Guid[]>(cancellationToken: token)
        ?? throw new InvalidOperationException("Resources returned an empty Agent list.");
    return Results.Ok(new AgentPackageUsageResponse(active.Length != 0));
});
app.MapGet("/api/v1/agent/queue-session", async (HttpContext context, HostHeartbeat service, CancellationToken token) =>
{
    var session = await service.GetSessionAsync((RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!, token);
    return session is null ? Results.Unauthorized() : Results.Ok(session);
});
app.MapPost("/api/v1/agent/queue-session", async (AgentQueueSessionRequest request, HttpContext context, HostHeartbeat service, CancellationToken token) =>
    await service.BeginSessionAsync((RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!, request, token) switch
    {
        AgentIdentityStatus.Success => Results.NoContent(),
        AgentIdentityStatus.Conflict => Results.Conflict(),
        _ => Results.Unauthorized()
    }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(1024));
app.MapGet("/api/v1/agent/settings", async (HttpContext context, HostAgentConfiguration service, CancellationToken token) =>
{
    var snapshot = await service.GetForAgentAsync((RegisteredAgentIdentity)context.Items[typeof(RegisteredAgentIdentity)]!, token);
    if (snapshot is null) return Results.Unauthorized();
    var tag = new EntityTagHeaderValue($"\"{snapshot.Revision}\"");
    context.Response.GetTypedHeaders().ETag = tag;
    context.Response.Headers.CacheControl = "private, no-store";
    var requested = context.Request.GetTypedHeaders().IfNoneMatch;
    if (requested?.Any(x => x.Equals(EntityTagHeaderValue.Any) || x.Compare(tag, useStrongComparison: false)) == true)
        return Results.StatusCode(StatusCodes.Status304NotModified);
    return Results.Ok(snapshot);
}).Produces<AgentSettingsSnapshot>().Produces(StatusCodes.Status304NotModified).ProducesProblem(401).ProducesProblem(503)
    .WithSummary("Read effective settings for the authenticated Agent");
app.MapGet("/internal/v1/security-batches/{agentId:guid}/{batchId:guid}", async (Guid agentId, Guid batchId,
    IDbContextFactory<HostMonitoringDbContext> contexts, CancellationToken token) =>
{
    await using var db = await contexts.CreateDbContextAsync(token);
    var payload = await db.HostSecurityBatches.AsNoTracking().Where(x => x.AgentId == agentId && x.BatchId == batchId)
        .Select(x => x.Payload).SingleOrDefaultAsync(token);
    return payload is null ? Results.NotFound() : Results.File(payload, "application/json");
});
app.MapDelete("/internal/v1/security-batches/{agentId:guid}/{batchId:guid}", async (Guid agentId, Guid batchId,
    IDbContextFactory<HostMonitoringDbContext> contexts, CancellationToken token) =>
{
    await using var db = await contexts.CreateDbContextAsync(token);
    await db.HostSecurityBatches.Where(x => x.AgentId == agentId && x.BatchId == batchId).ExecuteDeleteAsync(token);
    return Results.NoContent();
});
app.MapPost("/internal/v1/agents/{id:guid}/notifications/{kind}", async (Guid id, string kind, IHubContext<AgentChannel> hub, CancellationToken token) =>
{
    var method = kind switch { "update" => "UpdateAvailable", "scan" => "ScanAvailable", _ => null };
    if (method is null) return Results.BadRequest();
    await hub.Clients.Group(AgentChannel.Group(id)).SendAsync(method, token);
    return Results.Accepted();
});
app.Run();

public partial class Program;

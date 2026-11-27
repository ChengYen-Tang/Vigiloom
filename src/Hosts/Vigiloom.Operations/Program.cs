using Vigiloom.Infrastructure.Telemetry;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Operations;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Modules.AlertingOperations.Application.Retention;
using Vigiloom.Modules.AlertingOperations.Application.Timeline;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Operations;
using Vigiloom.Operations.HostLogs;
using Vigiloom.Operations.ApplicationLogs;
using Vigiloom.Operations.Work;
using QuotaContract = Vigiloom.Contracts.Operations.LogQuotaSettings;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAntiforgery();
builder.Services.AddOpenApi(options =>
{
    options.ShouldInclude = description => description.RelativePath?.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase) == true;
    options.AddDocumentTransformer<PublicApiOpenApi>();
    options.AddOperationTransformer<PublicApiOpenApiOperation>();
});
builder.AddVigiloomSelfTelemetry("Vigiloom.Operations");
builder.Services.AddVigiloomDataProtection(builder.Configuration);
builder.Services.AddOptions<IdentityOptions>();
builder.Services.AddVigiloomIdentityCookieClient();
builder.Services.AddVigiloomIdentityCredentialClient();
builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
builder.Services.AddAuthentication().AddVigiloomApiCredentials();
builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options =>
{
    options.Events.OnValidatePrincipal = context => context.HttpContext.RequestServices.GetRequiredService<IdentityCookieValidator>().ValidateAsync(context);
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddAuthorization(options => options.AddVigiloomAccessPolicies());
builder.Services.AddVigiloomApiRateLimiting(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddModuleSqlServer<OperationsDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("Operations")
        ?? throw new InvalidOperationException("ConnectionStrings:Operations is required."), "operations");
builder.Services.AddModuleSqlServer<SchedulingDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("Operations")
        ?? throw new InvalidOperationException("ConnectionStrings:Operations is required."), "scheduling");
builder.Services.AddModuleSqlServer<LogAdmissionDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("Operations")
        ?? throw new InvalidOperationException("ConnectionStrings:Operations is required."), "logquota");
builder.Services.AddModuleSqlServer<CentralLogsDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("Operations")
        ?? throw new InvalidOperationException("ConnectionStrings:Operations is required."), "logs", migrationHistory: "__EFMigrationsHistory_CentralLogs");
builder.Services.AddScoped<PolicyAdministration>();
builder.Services.AddScoped<IOperationAuditWriter<OperationAudit>, OperationAuditWriter>();
builder.Services.AddScoped<SharedLogQuota>();
builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233",
    builder.Configuration["Temporal:Namespace"] ?? "default");
builder.Services.AddScoped<NotificationWriter>();
builder.Services.AddScoped<OperationAuditWriter>();
builder.Services.AddScoped<TimelineWriter>();
builder.Services.AddScoped<TimelineReader>();
builder.Services.AddScoped<TimelineLogCleanup>();
builder.Services.AddServiceDiscovery();
if (!string.IsNullOrWhiteSpace(builder.Configuration["Services:Identity:Url"]))
{
    builder.Services.AddHttpClient<RemoteIdentityAuditRetention>((services, client) =>
        client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Identity:Url"]!.TrimEnd('/') + "/"))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
    builder.Services.AddScoped<IAuditRetentionSource>(services => services.GetRequiredService<RemoteIdentityAuditRetention>());
}
if (!string.IsNullOrWhiteSpace(builder.Configuration["Services:Resources:Url"]))
{
    builder.Services.AddHttpClient<RemoteResourcesAuditRetention>((services, client) =>
        client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Resources:Url"]!.TrimEnd('/') + "/"))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
    builder.Services.AddScoped<IAuditRetentionSource>(services => services.GetRequiredService<RemoteResourcesAuditRetention>());
}
if (!string.IsNullOrWhiteSpace(builder.Configuration["Services:HostMonitoring:Url"]))
{
    builder.Services.AddHttpClient<RemoteHostAuditRetention>((services, client) =>
        client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:HostMonitoring:Url"]!.TrimEnd('/') + "/"))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
    builder.Services.AddScoped<IAuditRetentionSource>(services => services.GetRequiredService<RemoteHostAuditRetention>());
}
if (!string.IsNullOrWhiteSpace(builder.Configuration["Services:Web:Url"]))
{
    builder.Services.AddHttpClient<RemotePlatformAuditRetention>((services, client) =>
        client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Web:Url"]!.TrimEnd('/') + "/"))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
    builder.Services.AddScoped<IAuditRetentionSource>(services => services.GetRequiredService<RemotePlatformAuditRetention>());
    builder.Services.AddScoped<ILegacyOperationsRetentionSource>(services => services.GetRequiredService<RemotePlatformAuditRetention>());
}
if (!string.IsNullOrWhiteSpace(builder.Configuration["Services:Security:Url"]))
{
    builder.Services.AddHttpClient<RemoteSecurityRetention>((services, client) =>
        client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Security:Url"]!.TrimEnd('/') + "/"))
        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
    builder.Services.AddScoped<IAuditRetentionSource>(services => services.GetRequiredService<RemoteSecurityRetention>());
}
foreach (var serviceName in new[] { "Resources", "Network", "Observability", "Security" })
{
    var name = serviceName;
    if (string.IsNullOrWhiteSpace(builder.Configuration[$"Services:{name}:Url"])) continue;
    builder.Services.AddHttpClient("work-history-" + name, (services, client) =>
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        client.BaseAddress = new Uri(configuration[$"Services:{name}:Url"]!.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(5);
    }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
    builder.Services.AddScoped<IBackgroundHistorySource>(services =>
        new RemoteWorkHistory(services.GetRequiredService<IHttpClientFactory>().CreateClient("work-history-" + name)));
}
builder.Services.AddHttpClient("Resources", (services, client) =>
{
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(5);
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddServiceDiscovery();
builder.Services.AddHttpClient("Observability", (services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Observability:Url"] ?? "http://vigiloom-observability").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
if (builder.Configuration.GetValue("Temporal:EnableOperationsWorker", true))
{
    builder.Services.AddVigiloomTemporalWorker<OperationsWorkHandler>(builder.Configuration["Work:OperationsQueue"] ?? Vigiloom.Contracts.Operations.OperationsWork.Queue);
    builder.Services.AddVigiloomTemporalWorker<OperationsWorkHandler>(builder.Configuration["Work:AuditQueue"] ?? "vigiloom.operations.audit");
}
builder.Services.AddHttpClient<LinePushClient>();
builder.Services.AddScoped<IOutboxHandler, LineDeliveryHandler>();
builder.Services.AddScoped<LineDeliveryQueue>();
builder.Services.AddScoped<LineChannel>();
builder.Services.AddScoped<LineDeliveryStatus>();
builder.Services.AddScoped<NotificationReader>();
builder.Services.AddScoped<BaselineExceptionReader>();
builder.Services.AddScoped<OperationAuditQueryService>();
builder.Services.AddSingleton(services =>
{
    var config = services.GetRequiredService<IConfiguration>();
    return new SchedulingReadStorage(config.GetConnectionString("Scheduling") ?? config.GetConnectionString("Operations")
        ?? throw new InvalidOperationException("ConnectionStrings:Scheduling is required."));
});
builder.Services.AddScoped<SchedulingOverview>();
builder.Services.AddSingleton<SchedulingHealth>();
builder.Services.AddScoped<WorkerDiagnosticsStore>();
builder.Services.AddScoped<NetworkDiagnosticsStore>();
builder.Services.AddHealthChecks().AddCheck<OperationsDatabaseHealth>("database");
builder.Services.AddScoped<AuditRetentionSettings>();
builder.Services.AddScoped<AuditRetentionCleanup>();
if (builder.Configuration.GetValue("Hangfire:Enabled", true))
{
    builder.Services.AddHangfire((services, settings) => settings.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
        .UseSqlServerStorage(services.GetRequiredService<IConfiguration>().GetConnectionString("Scheduling")
            ?? services.GetRequiredService<IConfiguration>().GetConnectionString("Operations")
            ?? throw new InvalidOperationException("ConnectionStrings:Scheduling is required."),
            SchedulingRegistration.StorageOptions(prepareSchema: false)));
    builder.Services.AddScoped<OperationsRetentionJob>();
    builder.Services.AddScoped<FailedJobRetention>();
    builder.Services.AddHostedService<OperationsRetentionSchedule>();
    builder.Services.AddHangfireServer(options =>
    {
        options.Queues = ["operations"];
        options.IsLightweightServer = true;
    });
}
builder.Services.AddScoped(provider => new OutboxDispatcher(
    provider.GetRequiredService<IConfiguration>().GetConnectionString("Operations")
        ?? throw new InvalidOperationException("ConnectionStrings:Operations is required."),
    provider.GetServices<IOutboxHandler>(), provider.GetRequiredService<ILogger<OutboxDispatcher>>()));
builder.Services.AddScoped<HostLogIngestion>();
builder.Services.AddScoped<HostLogReader>();
builder.Services.AddScoped<HostLogHistory>();
builder.Services.AddScoped<ApplicationLogIngestion>();
builder.Services.AddScoped<ApplicationLogHistory>();
builder.Services.AddScoped<ApplicationLogCleanup>();
builder.Services.AddHostedService<WorkDispatcher>();
builder.Services.AddHostedService<LineAggregationWorker>();
var app = builder.Build();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
var forwardedHeaders = TrustedProxyForwarding.Create(builder.Configuration);
if (forwardedHeaders is not null) app.UseForwardedHeaders(forwardedHeaders);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapHostLogQuery();
app.MapLineEndpoints();
app.MapPublicLineSettings();
app.MapHistoryEndpoints();
app.MapAuditEndpoints();
app.MapTimelineEndpoints();
app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready");
app.MapGet("/internal/v1/diagnostics/jobs", async (SchedulingOverview overview, CancellationToken token) =>
    Results.Ok(await overview.GetAsync(token)));
app.MapGet("/internal/v1/diagnostics/scheduler", async (SchedulingHealth scheduler, CancellationToken token) =>
{
    var result = await scheduler.ReadAsync(token);
    return Results.Ok(new SchedulerHeartbeatsInfo(result.ObservedAt, result.Values));
});
app.MapGet("/internal/v1/diagnostics/workers", async (NetworkDiagnosticsStore workers, CancellationToken token) => Results.Ok(await workers.ReadAsync(token)));
app.MapPost("/internal/v1/diagnostics/workers", async (NetworkDiagnosticUpdate input, NetworkDiagnosticsStore workers, CancellationToken token) =>
{
    try { await workers.SaveAsync(input, token); return Results.NoContent(); }
    catch (ArgumentException) { return Results.BadRequest(); }
});

// The deployment exposes this endpoint only to the trusted service network.
app.MapGet("/internal/v1/log-quota", async (SharedLogQuota quota, CancellationToken token) =>
    Results.Ok(Map(await quota.GetAsync(token))));
app.MapPost("/internal/v1/host-logs", async (HostLogSubmission input, HostLogIngestion ingestion, CancellationToken token) =>
    MapReceipt(await ingestion.ReceiveAsync(input, token)))
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2 * 1024 * 1024));
app.MapPost("/internal/v1/host-logs/read", async (HostLogReadRequest input, HostLogReader reader, CancellationToken token) =>
{
    if (input.AgentId.Version != 7 || input.MessageIds is null || input.MessageIds.Count > Vigiloom.Contracts.Agent.AgentLogBatchCodec.MaximumItems)
        return Results.BadRequest();
    return Results.Ok(await reader.ReadAsync(input.AgentId, input.MessageIds, token));
});
app.MapPost("/internal/v1/host-logs/identities", async (HostLogReadRequest input, IDbContextFactory<CentralLogsDbContext> contexts, CancellationToken token) =>
{
    if (input.AgentId.Version != 7 || input.MessageIds is null || input.MessageIds.Count > Vigiloom.Contracts.Agent.AgentLogBatchCodec.MaximumItems)
        return Results.BadRequest();
    await using var db = await contexts.CreateDbContextAsync(token);
    return Results.Ok(await db.HostLogs.AsNoTracking().Where(x => x.AgentId == input.AgentId && input.MessageIds.Contains(x.MessageId))
        .Select(x => x.MessageId).ToArrayAsync(token));
});
app.MapGet("/internal/v1/host-logs/{deviceId:guid}", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until,
    string? cursor, Guid? sourceId, HostLogHistory history, CancellationToken token) =>
{
    try { return Results.Ok(await history.GetAsync(deviceId, from, until, cursor, sourceId, token)); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
app.MapGet("/internal/v1/host-logs/{deviceId:guid}/{agentId:guid}/{messageId:guid}", async (Guid deviceId, Guid agentId, Guid messageId, HostLogHistory history, CancellationToken token) =>
    await history.GetEvidenceAsync(deviceId, agentId, messageId, token) is { } result ? Results.Ok(result) : Results.NotFound());
app.MapPost("/internal/v1/application-logs", async (ApplicationLogSubmission input, ApplicationLogIngestion ingestion, CancellationToken token) =>
    MapReceipt(await ingestion.ReceiveAsync(input, token)))
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8 * 1024 * 1024));
app.MapPost("/internal/v1/application-logs/batch", async (ApplicationLogSubmission[] input, ApplicationLogIngestion ingestion, CancellationToken token) =>
    MapReceipt(await ingestion.ReceiveBatchAsync(input, token)))
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8 * 1024 * 1024));
app.MapPost("/internal/v1/application-logs/grouped", async (ApplicationLogBatch input, ApplicationLogIngestion ingestion, CancellationToken token) =>
    MapReceipt(await ingestion.ReceiveGroupedAsync(input, token)))
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8 * 1024 * 1024));
app.MapGet("/internal/v1/application-logs/{serviceId:guid}", async (Guid serviceId, DateTimeOffset from, DateTimeOffset to,
    int? page, string? traceId, ApplicationLogHistory history, CancellationToken token) =>
{
    try { return Results.Ok(await history.GetAsync(serviceId, from, to, page ?? 1, traceId, token)); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
app.MapGet("/internal/v1/application-logs/{serviceId:guid}/latest", async (Guid serviceId, ApplicationLogHistory history, CancellationToken token) =>
    serviceId.Version != 7 ? Results.BadRequest() : Results.Ok(new { LastReceivedAt = await history.LastReceivedAsync(serviceId, token) }));
app.MapPost("/internal/v1/application-logs/latest-by-service", async (Guid[] serviceIds, ApplicationLogHistory history, CancellationToken token) =>
    Results.Ok(await history.LastReceivedAsync(serviceIds, token)));
app.MapGet("/internal/v1/application-logs/count", async (DateTimeOffset before, ApplicationLogHistory history, CancellationToken token) =>
    Results.Ok(new { Count = await history.CountBeforeAsync(before.ToUniversalTime(), token) }));
app.MapPost("/internal/v1/application-logs/cleanup", async (ApplicationLogCleanupRequest input, ApplicationLogCleanup cleanup, CancellationToken token) =>
{
    if (input.RetentionDays is < 1 or > 3650) return Results.BadRequest();
    return Results.Ok(await cleanup.RunAsync(input.RetentionDays, token));
});
app.MapPost("/internal/v1/timeline/health-cleanup", async (ApplicationLogCleanupRequest input, TimelineLogCleanup cleanup, CancellationToken token) =>
    input.RetentionDays is < 1 or > 3650 ? Results.BadRequest() : Results.Ok(await cleanup.RunHealthBatchAsync(input.RetentionDays, token)));
app.MapPost("/internal/v1/timeline/trace-cleanup", async (ApplicationLogCleanupRequest input, TimelineLogCleanup cleanup, CancellationToken token) =>
    input.RetentionDays is < 1 or > 3650 ? Results.BadRequest() : Results.Ok(await cleanup.RunTraceBatchAsync(input.RetentionDays, token)));
app.MapPut("/internal/v1/log-quota", async (LogQuotaUpdate input, SharedLogQuota quota, CancellationToken token) =>
{
    if (input.Settings is null || input.ActorId == Guid.Empty) return Results.BadRequest();
    var values = new LogQuotaValues(input.Settings.SourceRate, input.Settings.SourceBurst,
        input.Settings.GlobalRate, input.Settings.GlobalBurst);
    if (!values.IsValid) return Results.BadRequest();
    var updated = await quota.UpdateAsync(values, input.Settings.Version, input.ActorId, token);
    return updated is null ? Results.Conflict() : Results.Ok(Map(updated));
});
app.Run();

static QuotaContract Map(LogQuotaSnapshot value) =>
    new(value.Values.SourceRate, value.Values.SourceBurst, value.Values.GlobalRate, value.Values.GlobalBurst, value.Version);

static IResult MapReceipt(LogReceipt receipt) => receipt.Status switch
{
    LogReceiptStatus.Accepted => Results.Ok(receipt),
    LogReceiptStatus.Invalid => Results.BadRequest(receipt),
    LogReceiptStatus.Conflict => Results.Conflict(receipt),
    LogReceiptStatus.BatchTooLarge => Results.Json(receipt, statusCode: StatusCodes.Status413PayloadTooLarge),
    LogReceiptStatus.RateLimited => Results.Json(receipt, statusCode: StatusCodes.Status429TooManyRequests),
    _ => Results.Json(receipt, statusCode: StatusCodes.Status503ServiceUnavailable)
};

public partial class Program;

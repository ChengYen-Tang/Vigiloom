using Vigiloom.Infrastructure.Telemetry;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Security;
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
builder.AddVigiloomSelfTelemetry("Vigiloom.Operations");
builder.Services.AddVigiloomDataProtection(builder.Configuration);
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
builder.Services.AddHttpClient("Resources", (services, client) =>
{
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(5);
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .AddServiceDiscovery();
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
builder.Services.AddScoped<OperationAuditQueryService>();
builder.Services.AddSingleton(services =>
{
    var config = services.GetRequiredService<IConfiguration>();
    return new SchedulingReadStorage(config.GetConnectionString("Scheduling") ?? config.GetConnectionString("Operations")
        ?? throw new InvalidOperationException("ConnectionStrings:Scheduling is required."));
});
builder.Services.AddScoped<SchedulingOverview>();
builder.Services.AddSingleton(services =>
{
    var config = services.GetRequiredService<IConfiguration>();
    var connection = config.GetConnectionString("Scheduling") ?? config.GetConnectionString("Operations")
        ?? throw new InvalidOperationException("ConnectionStrings:Scheduling is required.");
    var options = new DbContextOptionsBuilder<SchedulingDbContext>()
        .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
            .MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
    return new SchedulingHealth(new PooledDbContextFactory<SchedulingDbContext>(options));
});
builder.Services.AddScoped<WorkerDiagnosticsStore>();
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
app.MapLineEndpoints();
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
app.MapGet("/internal/v1/diagnostics/workers", async (WorkerDiagnosticsStore workers, CancellationToken token) =>
{
    var result = await workers.GetSnapshotAsync(token);
    return Results.Ok(new WorkerDiagnosticsInfo(result.ObservedAt, result.Workers.Select(item => new WorkerDiagnosticInfo(
        item.BootId, item.ObservedAt, item.LeaseUntil, MapScanner(item.Observation.Ping), MapScanner(item.Observation.Speed))).ToArray()));
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
app.MapGet("/internal/v1/host-logs/{deviceId:guid}", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until,
    string? cursor, Guid? sourceId, HostLogHistory history, CancellationToken token) =>
{
    try { return Results.Ok(await history.GetAsync(deviceId, from, until, cursor, sourceId, token)); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
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
app.MapGet("/internal/v1/application-logs/count", async (DateTimeOffset before, ApplicationLogHistory history, CancellationToken token) =>
    Results.Ok(new { Count = await history.CountBeforeAsync(before.ToUniversalTime(), token) }));
app.MapPost("/internal/v1/application-logs/cleanup", async (ApplicationLogCleanupRequest input, ApplicationLogCleanup cleanup, CancellationToken token) =>
{
    if (input.RetentionDays is < 1 or > 3650) return Results.BadRequest();
    return Results.Ok(await cleanup.RunAsync(input.RetentionDays, token));
});
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

static ScannerDiagnosticInfo MapScanner(ScannerObservation value) =>
    new(value.LastSuccess, value.LastFailure, value.ConsecutiveFailures, (ScannerDiagnosticFailure)value.FailureKind);

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

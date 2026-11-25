using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Operations;
using Vigiloom.Operations.HostLogs;
using Vigiloom.Operations.ApplicationLogs;
using Vigiloom.Operations.Notifications;
using QuotaContract = Vigiloom.Contracts.Operations.LogQuotaSettings;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVigiloomDataProtection(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddModuleSqlServer<OperationsDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("Operations")
        ?? throw new InvalidOperationException("ConnectionStrings:Operations is required."), "operations");
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
if (builder.Configuration.GetValue("Temporal:EnableNotificationWorker", true))
    builder.Services.AddVigiloomTemporalWorker<NotificationWorkHandler>(builder.Configuration["Work:NotificationQueue"] ?? Vigiloom.Contracts.Notifications.NotificationWork.Queue);
builder.Services.AddHttpClient<LinePushClient>();
builder.Services.AddScoped<IOutboxHandler, LineDeliveryHandler>();
builder.Services.AddScoped<LineDeliveryQueue>();
builder.Services.AddScoped<LineChannel>();
builder.Services.AddScoped<LineDeliveryStatus>();
builder.Services.AddScoped(provider => new OutboxDispatcher(
    provider.GetRequiredService<IConfiguration>().GetConnectionString("Operations")
        ?? throw new InvalidOperationException("ConnectionStrings:Operations is required."),
    provider.GetServices<IOutboxHandler>(), provider.GetRequiredService<ILogger<OutboxDispatcher>>()));
builder.Services.AddScoped<HostLogIngestion>();
builder.Services.AddScoped<ApplicationLogIngestion>();
builder.Services.AddHostedService<WorkDispatcher>();
builder.Services.AddHostedService<LineAggregationWorker>();
var app = builder.Build();
app.MapLineEndpoints();

// The deployment exposes this endpoint only to the trusted service network.
app.MapGet("/internal/v1/log-quota", async (SharedLogQuota quota, CancellationToken token) =>
    Results.Ok(Map(await quota.GetAsync(token))));
app.MapPost("/internal/v1/host-logs", async (HostLogSubmission input, HostLogIngestion ingestion, CancellationToken token) =>
    MapReceipt(await ingestion.ReceiveAsync(input, token)))
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2 * 1024 * 1024));
app.MapPost("/internal/v1/application-logs", async (ApplicationLogSubmission input, ApplicationLogIngestion ingestion, CancellationToken token) =>
    MapReceipt(await ingestion.ReceiveAsync(input, token)))
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8 * 1024 * 1024));
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

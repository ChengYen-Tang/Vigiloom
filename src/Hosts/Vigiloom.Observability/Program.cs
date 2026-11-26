using Vigiloom.Contracts.Services;
using Vigiloom.Contracts.Resources;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Observability;

var builder = WebApplication.CreateBuilder(args);
var database = builder.Configuration.GetConnectionString("Observability")
    ?? throw new InvalidOperationException("ConnectionStrings:Observability is required.");
builder.Services.AddModuleSqlServer<ServiceObservabilityDbContext>(_ => database, "service", pooled: true);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddServiceDiscovery();
builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233",
    builder.Configuration["Temporal:Namespace"] ?? "default");
builder.Services.AddScoped(provider => new OutboxWriter(provider.GetServices<IOutboxHandler>(), maxPayloadLength: null));
builder.Services.AddHttpClient<TemporalHealthTransitionSink>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddSingleton<IHealthTransitionSink, HealthTransitionDispatcher>();
builder.Services.AddSingleton<HealthRunner>();
builder.Services.AddScoped<HealthStatusReader>();
builder.Services.AddScoped<HealthHistoryReader>();
builder.Services.AddScoped(provider => new OutboxDispatcher(database, provider.GetServices<IOutboxHandler>(),
    provider.GetRequiredService<ILogger<OutboxDispatcher>>()));
builder.Services.AddHostedService<HealthWorker>();
builder.Services.AddHostedService<HealthPolicyWorker>();
builder.Services.AddHostedService<HealthExpiryWorker>();
builder.Services.AddHostedService<WorkDispatcher>();
var app = builder.Build();
var internalApi = app.MapGroup("/internal/v1");
internalApi.MapGet("/observation-sources/{id:guid}/active", async (Guid id,
    IDbContextFactory<ServiceObservabilityDbContext> contexts, CancellationToken token) =>
{
    await using var db = await contexts.CreateDbContextAsync(token);
    var source = await db.Sources.AsNoTracking().Where(x => x.Id == id)
        .Select(x => new ApplicationSourceStatus(x.RevokedAt == null, x.Name)).SingleOrDefaultAsync(token);
    return Results.Ok(source ?? new ApplicationSourceStatus(false, null));
});
internalApi.MapGet("/health/{id:guid}/status", async (Guid id, HealthStatusReader reader, CancellationToken token) =>
{
    if (id.Version != 7) return Results.BadRequest();
    return await reader.ReadAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound();
});
internalApi.MapGet("/health/{id:guid}/history", async (Guid id, DateTimeOffset from, DateTimeOffset to,
    HealthHistoryReader reader, CancellationToken token) =>
{
    try
    {
        return await reader.ReadAsync(id, from.ToUniversalTime(), to.ToUniversalTime(), token) is { } result
            ? Results.Ok(result) : Results.NotFound();
    }
    catch (HealthHistoryRequestException) { return Results.BadRequest(); }
    catch (HealthHistoryTooLargeException) { return Results.Problem(statusCode: 422, title: "The requested history is too large; select a shorter time range."); }
    catch (HealthHistoryBusyException) { return Results.Problem(statusCode: 503, title: "Health history changed; retry the request."); }
});
app.Run();

public partial class Program;

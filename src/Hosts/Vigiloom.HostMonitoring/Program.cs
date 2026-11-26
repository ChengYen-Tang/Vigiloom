using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Vigiloom.Contracts.Agent;
using Vigiloom.HostMonitoring;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AgentManagement.Application;
using Hangfire;
using Hangfire.SqlServer;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
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
builder.Services.AddScoped<HostInboxCleanup>();
builder.Services.AddScoped<HostLogIdentityCleanup>();
builder.Services.AddScoped<HostWorkRetention>();
builder.Services.AddSingleton(TimeProvider.System);
if (builder.Configuration.GetValue("Hangfire:Enabled", true))
{
    builder.Services.AddHangfire((services, settings) => settings.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
        .UseSqlServerStorage(services.GetRequiredService<IConfiguration>().GetConnectionString("Scheduling")
            ?? throw new InvalidOperationException("ConnectionStrings:Scheduling is required."), SchedulingRegistration.StorageOptions(prepareSchema: false)));
    builder.Services.AddScoped<HostInboxRetentionJob>();
    builder.Services.AddHostedService<HostInboxRetentionSchedule>();
    builder.Services.AddHangfireServer(options => { options.Queues = ["host"]; options.IsLightweightServer = true; });
}
builder.Services.AddHttpClient("resources", (services, client) =>
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
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/v1/agent/channel") ||
    context.Request.Path.StartsWithSegments("/api/v1/agent/batches"), agent => agent.UseMiddleware<AgentAccess>());
app.MapHub<AgentChannel>("/api/v1/agent/channel");
app.MapAgentBatchEndpoints();
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

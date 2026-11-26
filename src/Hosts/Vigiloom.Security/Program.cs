using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Telemetry;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Security;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

var builder = WebApplication.CreateBuilder(args);
builder.AddVigiloomSelfTelemetry("Vigiloom.Security");
builder.Services.AddModuleSqlServer<SecurityAnalysisDbContext>(services =>
    services.GetRequiredService<IConfiguration>().GetConnectionString("Security")
        ?? throw new InvalidOperationException("ConnectionStrings:Security is required."), "security", pooled: true);
builder.Services.AddServiceDiscovery();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<YaraScans>();
builder.Services.AddScoped<AgentSecuritySettings>();
builder.Services.AddScoped<IBaselineSource, BaselineSource>();
builder.Services.AddScoped<InventoryConfiguration>();
builder.Services.AddScoped<InventoryQuery>();
builder.Services.AddScoped<InventoryChangeQuery>();
builder.Services.AddScoped<IntegrityConfiguration>();
builder.Services.AddScoped<IntegrityQuery>();
builder.Services.AddScoped<IntegrityChangeQuery>();
builder.Services.AddScoped<SigmaDetectionHistory>();
builder.Services.AddScoped<IocDetectionHistory>();
builder.Services.AddScoped<VulnerabilityMonitoring>();
builder.Services.AddScoped<IKevCatalog, KevCatalog>();
builder.Services.AddHttpClient<ResourceDevices>((services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IDeviceAgentSource>(services => services.GetRequiredService<ResourceDevices>());
builder.Services.AddScoped<IAgentOwnerResolver>(services => services.GetRequiredService<ResourceDevices>());
builder.Services.AddHttpClient("host", (services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Host:Url"] ?? "http://vigiloom-host-monitoring";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<IHostLogReader, OperationsHostLogs>((services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Services:Operations:Url"] ?? "http://vigiloom-operations";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
if (builder.Configuration.GetValue("Temporal:Enabled", true))
{
    builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233",
        builder.Configuration["Temporal:Namespace"] ?? "default");
    builder.Services.AddScoped<ISigmaCatalog, SigmaCatalog>();
    builder.Services.AddHostDetection();
    builder.Services.AddScoped<INotificationWriter, TemporalNotificationWriter>();
    builder.Services.AddVigiloomTemporalWorker<SecurityWorkHandler>(builder.Configuration["Work:SecurityQueue"] ?? "vigiloom.security.agent");
    builder.Services.AddVigiloomTemporalWorker<SecurityWorkHandler>(builder.Configuration["Work:SecurityLogQueue"] ?? "vigiloom.security.logs");
    builder.Services.AddScoped<IYaraScanNotifier, YaraScanNotifier>();
    builder.Services.AddScoped(provider => new OutboxDispatcher(provider.GetRequiredService<IConfiguration>().GetConnectionString("Security")
        ?? throw new InvalidOperationException("ConnectionStrings:Security is required."), provider.GetServices<IOutboxHandler>(),
        provider.GetRequiredService<ILogger<OutboxDispatcher>>()));
    builder.Services.AddHostedService<WorkDispatcher>();
}

var app = builder.Build();
app.UseWhen(context => context.Request.Path.StartsWithSegments("/internal/v1/yara-scans"), branch =>
    branch.Use(async (context, next) =>
    {
        try { await next(context); }
        catch (ArgumentException) { context.Response.StatusCode = StatusCodes.Status400BadRequest; }
    }));
app.MapGet("/health/live", () => Results.Ok());
var vulnerabilities = app.MapGroup("/internal/v1/devices/{deviceId:guid}/vulnerabilities");
vulnerabilities.MapGet("/", async (Guid deviceId, int page, int gapPage, string? search, int? state, string? rankingVersion, VulnerabilityMonitoring monitoring, CancellationToken token) =>
{
    try { return await monitoring.GetCurrentAsync(deviceId, page, search, state, token, gapPage, rankingVersion) is { } result ? Results.Ok(result) : Results.NotFound(); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
vulnerabilities.MapGet("/{findingId:guid}/history", async (Guid deviceId, Guid findingId, DateTimeOffset from, DateTimeOffset until, string? cursor, VulnerabilityMonitoring monitoring, CancellationToken token) =>
{
    try { return await monitoring.GetHistoryAsync(deviceId, findingId, from, until, cursor, token) is { } result ? Results.Ok(result) : Results.NotFound(); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
var detections = app.MapGroup("/internal/v1/devices/{deviceId:guid}/detections");
detections.MapGet("/sigma", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, SigmaDetectionHistory history, CancellationToken token) =>
{
    try { return await history.GetEventsAsync(deviceId, from, until, cursor, token) is { } page ? Results.Ok(page) : Results.NotFound(); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
detections.MapGet("/sigma-gaps", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, SigmaDetectionHistory history, CancellationToken token) =>
{
    try { return await history.GetGapsAsync(deviceId, from, until, cursor, token) is { } page ? Results.Ok(page) : Results.NotFound(); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
detections.MapGet("/ioc", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, int page, IocDetectionHistory history, CancellationToken token) =>
{
    try { return await history.GetAsync(deviceId, from, until, page, token) is { } result ? Results.Ok(result) : Results.NotFound(); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
app.MapGet("/internal/v1/agent-settings/{deviceId:guid}/inventory", async (Guid deviceId, AgentSecuritySettings settings, CancellationToken token) =>
    deviceId.Version == 7 ? Results.Ok(await settings.InventoryAsync(deviceId, token)) : Results.BadRequest());
app.MapGet("/internal/v1/agent-settings/{deviceId:guid}/integrity", async (Guid deviceId, AgentSecuritySettings settings, CancellationToken token) =>
    deviceId.Version == 7 ? Results.Ok(await settings.IntegrityAsync(deviceId, token)) : Results.BadRequest());
app.MapGet("/internal/v1/agent-settings/{deviceId:guid}/baseline", async (Guid deviceId, AgentSecuritySettings settings, CancellationToken token) =>
    deviceId.Version == 7 ? Results.Ok(await settings.BaselineAsync(deviceId, token)) : Results.BadRequest());
app.MapPost("/internal/v1/agent-integrity-status/{scopeId:guid}", async (Guid scopeId, RegisteredAgentIdentity identity, AgentSecuritySettings settings, CancellationToken token) =>
{
    if (scopeId.Version != 7 || identity.AgentId.Version != 7 || identity.DeviceId.Version != 7 || identity.DeviceGeneration < 0)
        return Results.BadRequest();
    try { return Results.Ok(await settings.IntegrityStatusAsync(identity, scopeId, token)); }
    catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
});
app.MapGet("/internal/v1/inventory-settings", async (Guid? deviceId, InventoryConfiguration settings, CancellationToken token) =>
    await settings.GetAsync(deviceId, token) is { } view ? Results.Ok(view) : Results.NotFound());
var inventory = app.MapGroup("/internal/v1/devices/{deviceId:guid}/inventory");
inventory.MapGet("/", (Guid deviceId, Guid? agentId, InventoryQuery query, CancellationToken token) => query.GetAsync(deviceId, agentId, token));
inventory.MapGet("/changes", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, InventorySource? source, string? cursor, InventoryChangeQuery query, CancellationToken token) =>
{
    try { return Results.Ok(await query.GetAsync(deviceId, from, until, source, cursor, token)); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
inventory.MapGet("/{source:int}/snapshots/{snapshotId:guid}/chunks/{chunkIndex:int}", async (Guid deviceId, Guid agentId, int source, Guid snapshotId, int chunkIndex, InventoryQuery query, CancellationToken token) =>
{
    try { return await query.GetChunkAsync(deviceId, agentId, (InventorySource)source, snapshotId, chunkIndex, token) is { } chunk ? Results.Ok(chunk) : Results.NotFound(); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
app.MapPut("/internal/v1/inventory-settings", async (Guid? deviceId, InventorySettingsCommand command, InventoryConfiguration settings, CancellationToken token) =>
{
    if (command.ActorId == Guid.Empty || !InventoryConfiguration.Valid(command.Update?.Overrides)) return Results.BadRequest();
    if (deviceId is not null && await settings.GetAsync(deviceId, token) is null) return Results.NotFound();
    return await settings.UpdateAsync(deviceId, command.Update!, command.ActorId, token) is { } saved
        ? Results.Ok(saved) : Results.Conflict();
});
var integrity = app.MapGroup("/internal/v1/devices/{deviceId:guid}/integrity-scopes");
var integrityHistory = app.MapGroup("/internal/v1/devices/{deviceId:guid}/integrity");
integrityHistory.MapGet("/", (Guid deviceId, Guid? agentId, IntegrityQuery query, CancellationToken token) => query.GetAsync(deviceId, agentId, token));
integrityHistory.MapGet("/changes", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, Guid? scopeId, string? cursor, IntegrityChangeQuery query, CancellationToken token) =>
{
    try { return Results.Ok(await query.GetAsync(deviceId, from, until, scopeId, cursor, token)); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
integrityHistory.MapGet("/scopes/{scopeId:guid}/snapshots/{snapshotId:guid}/chunks/{chunkIndex:int}", async (Guid deviceId, Guid agentId, Guid scopeId, Guid snapshotId, int chunkIndex, IntegrityQuery query, CancellationToken token) =>
{
    try { return await query.GetChunkAsync(deviceId, agentId, scopeId, snapshotId, chunkIndex, token) is { } chunk ? Results.Ok(chunk) : Results.NotFound(); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
integrity.MapGet("/", async (Guid deviceId, IntegrityConfiguration settings, CancellationToken token) =>
    await settings.ListAsync(deviceId, token) is { } scopes ? Results.Ok(scopes) : Results.NotFound());
integrity.MapPost("/", async (Guid deviceId, IntegrityCreateCommand command, IntegrityConfiguration settings, CancellationToken token) =>
{
    try { return Results.Ok(await settings.CreateAsync(deviceId, command.Policy, command.ActorId, token)); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
integrity.MapPut("/{scopeId:guid}", async (Guid deviceId, Guid scopeId, IntegrityUpdateCommand command, IntegrityConfiguration settings, CancellationToken token) =>
{
    try { return Results.Ok(await settings.UpdateAsync(deviceId, scopeId, command.Policy, command.Version, command.ActorId, token)); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
integrity.MapPost("/{scopeId:guid}/retire", async (Guid deviceId, Guid scopeId, IntegrityRetireCommand command, IntegrityConfiguration settings, CancellationToken token) =>
{
    try { return Results.Ok(await settings.RetireAsync(deviceId, scopeId, command.Version, command.ActorId, token)); }
    catch (ArgumentException) { return Results.BadRequest(); }
});
var yara = app.MapGroup("/internal/v1/yara-scans");
yara.MapPost("/create", async (YaraCreateCommand command, YaraScans scans, CancellationToken token) =>
    Results.Ok(await scans.CreateAsync(command.Identity, command.Request, command.ActorId, token)));
yara.MapPost("/next", async (RegisteredAgentIdentity identity, YaraScans scans, CancellationToken token) =>
    await scans.NextAsync(identity, token) is { } job ? Results.Ok(job) : Results.NoContent());
yara.MapGet("/devices/{deviceId:guid}", async (Guid deviceId, YaraScans scans, CancellationToken token) =>
    Results.Ok(await scans.ListAsync(deviceId, token)));
yara.MapGet("/devices/{deviceId:guid}/{scanId:guid}", async (Guid deviceId, Guid scanId, YaraScans scans, CancellationToken token) =>
    await scans.GetAsync(deviceId, scanId, token) is { } job ? Results.Ok(job) : Results.NotFound());
yara.MapPost("/devices/{deviceId:guid}/{scanId:guid}/cancel", async (Guid deviceId, Guid scanId, YaraScans scans, CancellationToken token) =>
    await scans.CancelAsync(deviceId, scanId, token) ? Results.NoContent() : Results.NotFound());
app.Run();

public partial class Program;

internal sealed record InventorySettingsCommand(InventorySettingsUpdate Update, Guid ActorId);
internal sealed record IntegrityCreateCommand(IntegrityScopeInput Policy, Guid ActorId);
internal sealed record IntegrityUpdateCommand(IntegrityScopeInput Policy, string? Version, Guid ActorId);
internal sealed record IntegrityRetireCommand(string? Version, Guid ActorId);

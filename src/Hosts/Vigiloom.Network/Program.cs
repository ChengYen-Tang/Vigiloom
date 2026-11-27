using Vigiloom.Infrastructure.Telemetry;
using Hangfire;
using Hangfire.SqlServer;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Network;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi(options =>
{
    options.ShouldInclude = description => description.RelativePath?.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase) == true;
    options.AddDocumentTransformer<PublicApiOpenApi>();
    options.AddOperationTransformer<PublicApiOpenApiOperation>();
});
builder.AddVigiloomSelfTelemetry("Vigiloom.Network");
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
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnValidatePrincipal = context => context.HttpContext.RequestServices.GetRequiredService<IdentityCookieValidator>().ValidateAsync(context);
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddAuthorization(options => options.AddVigiloomAccessPolicies());
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("api-client", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Clamp(builder.Configuration.GetValue("Vigiloom:Authentication:ApiRequestsPerMinute", 300), 1, 10000),
            Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});
builder.Services.AddModuleSqlServer<NetworkDbContext>(services => services.GetRequiredService<IConfiguration>().GetConnectionString("Network")
    ?? throw new InvalidOperationException("ConnectionStrings:Network is required."), "network", pooled: true);
builder.Services.AddModuleSqlServer<SchedulingDbContext>(services => services.GetRequiredService<IConfiguration>().GetConnectionString("Network")
    ?? throw new InvalidOperationException("ConnectionStrings:Network is required."), "scheduling");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NetworkDiagnostics>();
builder.Services.AddHttpClient("diagnostics", (services, client) =>
{
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Operations:Url"] ?? "http://vigiloom-operations").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(5);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHostedService<NetworkDiagnosticsPublisher>();
builder.Services.AddServiceDiscovery();
builder.Services.AddHttpClient<IDevicePingSource, RemoteDevicePingSource>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<ResourceDevices>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<PublicPing>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233", builder.Configuration["Temporal:Namespace"] ?? "default");
builder.Services.AddScoped(_ => new WorkHistoryRetention(builder.Configuration.GetConnectionString("Network")
    ?? throw new InvalidOperationException("ConnectionStrings:Network is required.")));
if (builder.Configuration.GetValue("Temporal:EnableNetworkWorker", true))
    builder.Services.AddVigiloomTemporalWorker<DeviceChangeHandler>(builder.Configuration["Work:NetworkQueue"] ?? "vigiloom.network.resources");
builder.Services.AddScoped<IOperationAuditWriter<OperationAudit>, TemporalAuditWriter>();
builder.Services.AddScoped<IPingTransitionSink, TemporalPingTransitionSink>();
builder.Services.AddScoped<PingSettings>();
builder.Services.AddScoped<PingHistoryReader>();
builder.Services.AddScoped<PingRetentionSettings>();
builder.Services.AddScoped<PingStore>();
builder.Services.AddScoped<IPingProbe, SystemPingProbe>();
builder.Services.AddScoped<PingRunner>();
builder.Services.AddScoped<SpeedSettings>();
builder.Services.AddScoped<SpeedHistory>();
builder.Services.AddScoped<SpeedRetentionSettings>();
builder.Services.AddScoped<SpeedRetentionCleanup>();
builder.Services.AddScoped<PingRetentionCleanup>();
builder.Services.AddScoped<PingRollupBackfill>();
builder.Services.AddScoped<SpeedQueue>();
builder.Services.AddScoped<SpeedRunner>();
builder.Services.AddHttpClient(SpeedtestNetProbe.HttpClientName).ConfigurePrimaryHttpMessageHandler(SpeedtestNetProbe.CreatePrimaryHandler);
builder.Services.AddScoped<ISpeedProbe, SpeedtestNetProbe>();
builder.Services.AddScoped(provider => new OutboxDispatcher(provider.GetRequiredService<IConfiguration>().GetConnectionString("Network")
    ?? throw new InvalidOperationException("ConnectionStrings:Network is required."), provider.GetServices<IOutboxHandler>(), provider.GetRequiredService<ILogger<OutboxDispatcher>>()));
builder.Services.AddHostedService<PingWorker>();
builder.Services.AddHostedService<SpeedWorker>();
builder.Services.AddHostedService<WorkDispatcher>();
if (builder.Configuration.GetValue("Hangfire:Enabled", true))
{
    builder.Services.AddHangfire((services, settings) => settings.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
        .UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
        .UseSqlServerStorage(services.GetRequiredService<IConfiguration>().GetConnectionString("Scheduling")
            ?? throw new InvalidOperationException("ConnectionStrings:Scheduling is required."), SchedulingRegistration.StorageOptions(prepareSchema: false)));
    builder.Services.AddScoped<NetworkAutomaticSpeedJob>();
    builder.Services.AddScoped<NetworkPingRetentionJob>();
    builder.Services.AddScoped<NetworkPingRollupJob>();
    builder.Services.AddScoped<NetworkSpeedRetentionJob>();
    builder.Services.AddHostedService<NetworkMaintenanceSchedule>();
    builder.Services.AddHostedService<SpeedSchedule>();
    builder.Services.AddHangfireServer(options => { options.Queues = ["network"]; options.IsLightweightServer = true; });
}
var app = builder.Build();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapWorkHistoryEndpoints();
var forwardedHeaders = TrustedProxyForwarding.Create(builder.Configuration);
if (forwardedHeaders is not null) app.UseForwardedHeaders(forwardedHeaders);
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapSpeedTestEndpoints();
app.MapPingEndpoints();
app.MapPublicPingEndpoints();
app.MapInternalSpeedAdministration();
app.MapInternalPingAdministration();
app.MapGet("/internal/v1/ping/{id:guid}/publication-preview", async (Guid id, PublicPing reader, CancellationToken token) =>
    await reader.PreviewAsync(id, token) is { } preview ? Results.Ok(preview) : Results.NotFound());
app.MapPost("/internal/v1/ping/status", async (PingStatusRequest input, PingStore store, CancellationToken token) =>
    Results.Ok(await store.GetStatusAsync(input.DeviceId, input.Targets, input.DeviceVersion, token)));
app.MapPost("/internal/v1/ping/statuses", async (PingDeviceSnapshot[] inputs, PingStore store, CancellationToken token) =>
    inputs.Length > 250 ? Results.BadRequest() : Results.Ok(await store.GetStatusesAsync(inputs, token)));
app.MapPost("/internal/v1/ping/history", async (PingHistoryQuery query, PingHistoryReader history, CancellationToken token) =>
    PingHistoryReader.Valid(query) ? Results.Ok(await history.QueryAsync(query, token)) : Results.BadRequest());
app.MapPost("/internal/v1/speed/history", async (Vigiloom.Contracts.SpeedTests.SpeedHistoryQuery query, SpeedHistory history, CancellationToken token) =>
    SpeedHistory.Valid(query) ? Results.Ok(await history.QueryAsync(query, token)) : Results.BadRequest());
app.MapGet("/internal/v1/speed/history/{id:guid}", async (Guid id, SpeedHistory history, CancellationToken token) =>
    await history.GetAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound());
app.MapGet("/health/live", () => Results.Ok());
app.Run();

public partial class Program;

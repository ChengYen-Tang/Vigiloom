using Vigiloom.Infrastructure.Telemetry;
using Vigiloom.Contracts.Services;
using Vigiloom.Contracts.Resources;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Observability;
using Vigiloom.Observability.Authentication;
using Vigiloom.Observability.Telemetry;

var builder = WebApplication.CreateBuilder(args);
builder.AddVigiloomSelfTelemetry("Vigiloom.Observability");
var database = builder.Configuration.GetConnectionString("Observability")
    ?? throw new InvalidOperationException("ConnectionStrings:Observability is required.");
builder.Services.AddModuleSqlServer<ServiceObservabilityDbContext>(_ => database, "service", pooled: true);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<SourceCredentials>();
builder.Services.AddScoped<SelfSourceProvider>();
builder.Services.AddSingleton(provider => new ReceptionRejectionDiagnostics(
    () => provider.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>(),
    provider.GetRequiredService<ILogger<ReceptionRejectionDiagnostics>>()));
builder.Services.AddHostedService(provider => provider.GetRequiredService<ReceptionRejectionDiagnostics>());
builder.Services.AddAuthentication().AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ServiceSourceCredentialHandler>(
    ServiceSourceCredentialHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options => options.AddPolicy("ServiceSource", policy =>
    policy.AddAuthenticationSchemes(ServiceSourceCredentialHandler.SchemeName).RequireAuthenticatedUser()));
builder.Services.AddServiceDiscovery();
builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233",
    builder.Configuration["Temporal:Namespace"] ?? "default");
builder.Services.AddVigiloomTemporalWorker<ApplicationEventHandler>(builder.Configuration["Work:ObservabilityQueue"] ?? "vigiloom.observability.catalog");
builder.Services.AddScoped<ApplicationStateProjection>();
builder.Services.AddHttpClient("resources", (services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped(provider => new OutboxWriter(provider.GetServices<IOutboxHandler>(), maxPayloadLength: null));
builder.Services.AddHttpClient<TemporalHealthTransitionSink>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<ICandidateObserver, RemoteCandidateObserver>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<SelfCandidateObserver>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Resources:Url"] ?? "http://vigiloom-resources").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<IApplicationLogDelivery, ApplicationLogDelivery>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Operations:Url"] ?? "http://vigiloom-operations").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<TelemetryExports>();
builder.Services.AddSingleton<OtlpConcurrency>();
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
var forwarded = TrustedProxyForwarding.Create(builder.Configuration);
if (forwarded is not null) app.UseForwardedHeaders(forwarded);
app.UseWhen(context => context.Request.Path.StartsWithSegments(OtlpHttpEndpoints.Prefix) ||
    context.Request.Path.StartsWithSegments(SelfOtlpEndpoints.Prefix), otlp =>
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
    }));
app.UseMiddleware<OtlpTransportGuard>();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/internal/telemetry/source/identity", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Ok(new { SourceId = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value });
}).RequireAuthorization("ServiceSource");
var internalApi = app.MapGroup("/internal/v1");
app.MapOtlpHttpEndpoints();
app.MapSelfOtlpEndpoints();
app.MapPost("/api/v1/service-discovery/observations", (HttpContext context, ICandidateObserver candidates,
    ReceptionRejectionDiagnostics diagnostics, CancellationToken token) =>
    ServiceObservationEndpoint.ReceiveAsync(context, candidates, token, diagnostics))
    .RequireAuthorization("ServiceSource")
    .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ServiceObservationEndpoint.MaximumBytes));
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

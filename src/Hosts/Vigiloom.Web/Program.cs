using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Web.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Contracts.Operations;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.Components;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Api;
using MudBlazor.Services;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
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

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization();
builder.Services.AddModuleSqlServer<IdentityAccessDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "identity");
builder.Services.AddModuleSqlServer<ResourcesDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "resources");
builder.Services.AddModuleSqlServer<AgentManagementDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "agent");
builder.Services.AddModuleSqlServer<ServiceObservabilityDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "service");
builder.Services.AddModuleSqlServer<NetworkDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "network", pooled: true);
builder.Services.AddModuleSqlServer<SchedulingDbContext>(_ => SchedulingRegistration.Connection(builder.Configuration), "scheduling");
builder.Services.AddModuleSqlServer<OperationsDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "operations");
builder.Services.AddModuleSqlServer<LogAdmissionDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "logquota");
builder.Services.AddScoped<PolicyAdministration>();
builder.Services.AddScoped<TracePolicyAdministration>();
builder.Services.AddScoped<Vigiloom.Web.LogQuota.LogQuotaSettings>();
builder.Services.AddScoped<TraceQuotaSettings>();
builder.Services.AddScoped<IOperationAuditWriter<OperationAudit>, OperationAuditWriter>();
builder.Services.AddScoped<OperationAuditQueryService>();
builder.Services.AddScoped<IdentityAuditFeed>();
builder.Services.AddScoped<DeviceAuditFeed>();
builder.Services.AddScoped<AgentAuditFeed>();
builder.Services.AddScoped<AuditFeedReader>();
builder.Services.AddVigiloomScheduling(builder.Configuration);
builder.Services.AddVigiloomDataProtection(builder.Configuration);
builder.Services.AddVigiloomIdentity();
builder.Services.AddMudServices();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DeviceManagement>();
builder.Services.AddScoped<GroupManagement>();
builder.Services.AddScoped<ServiceMembership>();
builder.Services.AddScoped<IDevicePingSource, DevicePingSource>();
builder.Services.AddScoped<IDeviceAgentSource, DeviceAgentSource>();
builder.Services.AddScoped<AgentIdentity>();
builder.Services.AddScoped<CandidateDiscovery>();
builder.Services.AddScoped<TelemetryExports>();
builder.Services.AddScoped<TelemetryHistory>();
builder.Services.AddScoped<ReceptionOverview>();
builder.Services.AddScoped<ReceptionRejectionReader>();
builder.Services.AddScoped<CollectorObservations>();
builder.Services.AddScoped<CollectorCounters>();
builder.Services.AddScoped<CollectorAdministration>();
builder.Services.AddSingleton<OtlpConcurrency>();
builder.Services.AddSingleton(provider => new ReceptionRejectionDiagnostics(
    () => provider.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>(),
    provider.GetRequiredService<ILogger<ReceptionRejectionDiagnostics>>()));
builder.Services.AddHostedService(provider => provider.GetRequiredService<ReceptionRejectionDiagnostics>());
builder.Services.AddScoped<ServiceApproval>();
builder.Services.AddScoped<ServiceInstanceAdministration>();
builder.Services.AddScoped<Vigiloom.Modules.ServiceObservability.Application.Health.HealthTargetAdministration>();
builder.Services.AddScoped<Vigiloom.Modules.ServiceObservability.Application.Health.HealthNetworkPolicyAdministration>();
builder.Services.AddScoped<Vigiloom.Modules.ServiceObservability.Application.Health.HealthObservationReader>();
builder.Services.AddScoped<Vigiloom.Modules.ServiceObservability.Application.Health.HealthStatusReader>();
builder.Services.AddScoped<Vigiloom.Modules.ServiceObservability.Application.Health.HealthHistoryReader>();
builder.Services.AddScoped<InstancePolicyAdministration>();
builder.Services.AddScoped<ServiceCollectionAdministration>();
builder.Services.AddScoped<SourceCredentials>();
builder.Services.AddScoped<SourceAdministration>();
builder.Services.AddScoped<AgentMonitoring>();
builder.Services.AddScoped<AgentConfiguration>();
builder.Services.AddScoped<AgentQueueCapacityConfiguration>();
builder.Services.AddScoped<AgentReplayPolicyService>();
builder.Services.AddScoped<AgentQueueCapacityMonitoring>();
builder.Services.AddScoped<AgentSettingsMonitoring>();
builder.Services.AddScoped<AgentBatchReceiver>();
builder.Services.AddScoped<AgentReceiverMonitoring>();
builder.Services.AddScoped<WorkerDiagnosticsStore>();
builder.Services.AddScoped<PlatformStatus>();
builder.Services.AddSingleton<AgentTransportConcurrency>();
builder.Services.AddScoped<PingSettings>();
builder.Services.AddScoped<PingStore>();
builder.Services.AddScoped<SpeedSettings>();
builder.Services.AddScoped<SpeedQueue>();
builder.Services.AddScoped<SpeedHistory>();
builder.Services.AddScoped<ISpeedHistoryClient, ServerSpeedHistoryClient>();
builder.Services.AddScoped<WorkspaceIdentityService>();
builder.Services.AddScoped<IWorkspaceIdentityClient, ServerWorkspaceIdentityClient>();
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

app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/admin/jobs"), web =>
    web.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseRouting();
app.UseMiddleware<OtlpTransportGuard>();
app.UseMiddleware<AgentTransportGuard>();
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/workspace") || context.Request.Path.StartsWithSegments("/settings") || context.Request.Path.StartsWithSegments("/devices") || context.Request.Path.StartsWithSegments("/groups") || context.Request.Path.StartsWithSegments("/speed-tests"))
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
app.MapAccountEndpoints();
app.MapDeviceEndpoints();
app.MapGroupEndpoints();
app.MapAgentEndpoints();
app.MapAgentConfigurationEndpoints();
app.MapAgentQueueCapacityEndpoints();
app.MapAgentReplayPolicyEndpoints();
app.MapAgentBatchEndpoints();
app.MapServiceDiscoveryEndpoints();
app.MapOtlpHttpEndpoints();
app.MapTelemetryHistoryEndpoints();
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

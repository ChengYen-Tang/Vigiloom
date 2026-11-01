using System.Threading.RateLimiting;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Web.Components;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Api;
using MudBlazor.Services;
using Vigiloom.Contracts.Workspace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization();
builder.Services.AddModuleSqlServer<IdentityAccessDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "identity");
builder.Services.AddModuleSqlServer<ResourcesDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "resources");
builder.Services.AddModuleSqlServer<NetworkDbContext>(_ => builder.Configuration.GetConnectionString("Vigiloom") ?? throw new InvalidOperationException("ConnectionStrings:Vigiloom is required."), "network");
builder.Services.AddVigiloomDataProtection(builder.Configuration);
builder.Services.AddVigiloomIdentity();
builder.Services.AddMudServices();
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
builder.Services.AddHealthChecks();

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
    api.UseStatusCodePages();
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

app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api"), web =>
    web.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.UseHttpsRedirection();
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/workspace")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseAuthorization();
app.UseAntiforgery();

app.MapHealthChecks("/health/live").AllowAnonymous();
app.MapAccountEndpoints();
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

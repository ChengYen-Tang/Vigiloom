using Vigiloom.Infrastructure.Telemetry;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Resources;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Security;
using Vigiloom.UpdateSecurity.AgentPackages;
using Vigiloom.Modules.Resources.Application.AgentPackages;
using Vigiloom.Resources.AgentPackages;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.Resources.Application.AgentIdentity;
using Vigiloom.Modules.Resources.Application.Publishing;
using Vigiloom.Resources.Publication;
using Vigiloom.Resources.DeploymentLinks;
using Vigiloom.Contracts.Services;
using Vigiloom.Contracts.Operations;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.AddVigiloomSelfTelemetry("Vigiloom.Resources");
builder.Services.AddVigiloomDataProtection(builder.Configuration);
builder.Services.AddOptions<IdentityOptions>();
builder.Services.AddAntiforgery();
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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too Many Requests",
            Extensions = { ["code"] = "too_many_requests" }
        }, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
    };
    options.AddPolicy("api-client", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Clamp(builder.Configuration.GetValue("Vigiloom:Authentication:ApiRequestsPerMinute", 300), 1, 10000),
            Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});
builder.Services.AddVigiloomTemporalClient(builder.Configuration["Temporal:Address"] ?? "localhost:7233",
    builder.Configuration["Temporal:Namespace"] ?? "default");
builder.Services.AddScoped(_ => new WorkHistoryRetention(builder.Configuration.GetConnectionString("Resources")
    ?? throw new InvalidOperationException("ConnectionStrings:Resources is required.")));
builder.Services.AddScoped<ResourceWork>();
builder.Services.AddScoped(provider => new OutboxDispatcher(provider.GetRequiredService<IConfiguration>().GetConnectionString("Resources")
    ?? throw new InvalidOperationException("ConnectionStrings:Resources is required."), provider.GetServices<IOutboxHandler>(),
    provider.GetRequiredService<ILogger<OutboxDispatcher>>()));
builder.Services.AddHostedService<WorkDispatcher>();
builder.Services.AddModuleSqlServer<ResourcesDbContext>(
    _ => builder.Configuration.GetConnectionString("Resources")
        ?? throw new InvalidOperationException("ConnectionStrings:Resources is required."), "resources", pooled: true);
builder.Services.AddModuleSqlServer<ResourceDeploymentLinksDbContext>(
    _ => builder.Configuration.GetConnectionString("Resources")
        ?? throw new InvalidOperationException("ConnectionStrings:Resources is required."), "resources", pooled: true,
    migrationHistory: ResourceDeploymentLinksDbContext.MigrationHistory);
builder.Services.AddServiceDiscovery();
builder.Services.AddHttpClient<ResourceDeploymentLinks>((services, client) =>
    client.BaseAddress = new Uri((services.GetRequiredService<IConfiguration>()["Services:Observability:Url"]
        ?? "http://vigiloom-observability").TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<DevicePingSource>();
builder.Services.AddScoped<DeviceAgentSource>();
builder.Services.AddScoped<GroupMembersReader>();
builder.Services.AddScoped<ServiceGroupsReader>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DeviceManagement>();
builder.Services.AddScoped<DeviceAuditFeed>();
builder.Services.AddScoped<DeviceChangeOutbox>();
builder.Services.AddScoped<GroupManagement>();
builder.Services.AddScoped<GroupAuditOutbox>();
builder.Services.AddScoped<ServiceGroupManagement>();
builder.Services.AddScoped<ApplicationDiscovery>();
builder.Services.AddScoped<ApplicationApproval>();
builder.Services.AddScoped<SelfServiceRegistration>();
builder.Services.AddScoped<ApplicationLifecycle>();
builder.Services.AddScoped<ApplicationCatalog>();
builder.Services.AddScoped<PackageCatalog>();
builder.Services.AddHttpClient<IPackageRuntimeUsage, PackageRuntimeUsage>().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient<IHostAgentVersion, HostAgentVersion>().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<AgentAssignments>();
builder.Services.AddScoped<AgentPackageAccess>();
builder.Services.AddScoped<AgentRegistration>();
builder.Services.AddScoped<PublicTemplates>();
builder.Services.AddScoped<TemplateChanges>();
builder.Services.AddScoped<PublishedPolicies>();
builder.Services.AddScoped<PublicationSettingsManager>();
builder.Services.AddHttpClient<GitHubAgentReleases>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Vigiloom");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<IPackageLibraryTransport>(services =>
{
    var directory = services.GetRequiredService<IConfiguration>()["Vigiloom:AgentPackages:CachePath"];
    if (string.IsNullOrWhiteSpace(directory))
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData)) throw new InvalidOperationException("Set Vigiloom:AgentPackages:CachePath.");
        directory = Path.Combine(localData, "Vigiloom", "agent-packages");
    }
    return new PackageLibraryTransport(directory);
});
builder.Services.AddHttpClient<ObservationSourceVerifier>((services, client) =>
    client.BaseAddress = new Uri(services.GetRequiredService<IConfiguration>()["Services:Observability:Url"]!.TrimEnd('/') + "/"))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddScoped<IApplicationSourceVerifier>(services => services.GetRequiredService<ObservationSourceVerifier>());

var app = builder.Build();
var forwardedHeaders = TrustedProxyForwarding.Create(builder.Configuration);
if (forwardedHeaders is not null) app.UseForwardedHeaders(forwardedHeaders);
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapWorkHistoryEndpoints();
app.MapPost("/internal/v1/devices/audit", async (OperationAuditQuery query, DeviceAuditFeed feed, CancellationToken token) =>
    AuditFeedLimits.Valid(query) ? Results.Ok(await feed.QueryAsync(query, token)) : Results.BadRequest());
var internalApi = app.MapGroup("/internal/v1");
internalApi.MapGet("/application-services/{id:guid}/deployments", async (Guid id, ResourceDeploymentLinks links, CancellationToken token, int page = 1) =>
    await links.ListAsync(id, page, token) is { } result ? Results.Ok(result) : Results.NotFound());
internalApi.MapPost("/application-services/{id:guid}/deployments", (Guid id, CreateDeploymentLinkCommand command, ResourceDeploymentLinks links, CancellationToken token) =>
    links.CreateAsync(id, command, token));
internalApi.MapPost("/application-services/{id:guid}/deployments/{linkId:guid}/end", (Guid id, Guid linkId, EndDeploymentLinkCommand command, ResourceDeploymentLinks links, CancellationToken token) =>
    links.EndAsync(id, linkId, command, token));
internalApi.MapGet("/ping-targets", (DevicePingSource source, CancellationToken token) => source.ReadEnabledAsync(token));
internalApi.MapPost("/ping-targets/verify", async (PingTargetVerification target, DevicePingSource source, CancellationToken token) =>
    await source.VerifyAsync(target, token) is { } context ? Results.Ok(context) : Results.NotFound());
internalApi.MapGet("/ping-targets/{id:guid}/context", (Guid id, DevicePingSource source, CancellationToken token) => source.GetContextAsync(id, token));
internalApi.MapGet("/devices/{id:guid}/agent", async (Guid id, DeviceAgentSource source, CancellationToken token) =>
    await source.GetAsync(id, token) is { } device ? Results.Ok(device) : Results.NotFound());
internalApi.MapGet("/groups/{id:guid}/members", async (Guid id, GroupMembersReader source, CancellationToken token) =>
    await source.GetAsync(id, token) is { } members ? Results.Ok(members) : Results.NotFound());
internalApi.MapGet("/application-services/{id:guid}/groups", (Guid id, ServiceGroupsReader source, CancellationToken token) =>
    source.ReadAsync(id, token));
internalApi.MapGet("/publications/groups/{id:guid}", async (Guid id, PublishedPolicies policies, CancellationToken token) =>
    await policies.GroupAsync(id, token) is { } policy ? Results.Ok(policy) : Results.NotFound());
internalApi.MapGet("/publications/groups/{id:guid}/summary", async (Guid id, PublishedPolicies policies, CancellationToken token) =>
    await policies.GroupSummaryAsync(id, token) is { } group ? Results.Ok(group) : Results.NotFound());
internalApi.MapGet("/publications/groups", async (string? search, int? page, PublishedPolicies policies, CancellationToken token) =>
    page is < 1 or > 1000 ? Results.BadRequest() : Results.Ok(await policies.GroupsAsync(search, page ?? 1, token)));
internalApi.MapGet("/publications/devices", async (string? search, int? page, Guid? groupId, PublishedPolicies policies, CancellationToken token) =>
    page is < 1 or > 1000 ? Results.BadRequest() : Results.Ok(await policies.DevicesAsync(search, page ?? 1, groupId, token)));
internalApi.MapGet("/publications/services", async (string? search, int? page, Guid? groupId, PublishedPolicies policies, CancellationToken token) =>
    page is < 1 or > 1000 ? Results.BadRequest() : Results.Ok(await policies.ServicesAsync(search, page ?? 1, groupId, token)));
internalApi.MapGet("/publications/devices/{id:guid}", async (Guid id, PublishedPolicies policies, CancellationToken token) =>
    await policies.DeviceAsync(id, token) is { } policy ? Results.Ok(policy) : Results.NotFound());
internalApi.MapGet("/publications/services/{id:guid}", async (Guid id, PublishedPolicies policies, CancellationToken token) =>
    await policies.ServiceAsync(id, token) is { } policy ? Results.Ok(policy) : Results.NotFound());
internalApi.MapInternalDeviceManagement();
internalApi.MapInternalPublicationManagement();
internalApi.MapInternalGroupManagement();
internalApi.MapInternalPackages();
internalApi.MapInternalAgentRegistration();
internalApi.MapPost("/application-candidates/observe", async (ApplicationObservation request, ApplicationDiscovery discovery, CancellationToken token) =>
{
    try { return Results.Ok(await discovery.ObserveAsync(request, token)); }
    catch (ApplicationDiscoveryBusyException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
});
internalApi.MapPost("/self-services/register", async (ApplicationObservation request, SelfServiceRegistration registration, CancellationToken token) =>
    await registration.RegisterAsync(request, token) is { } id ? Results.Ok(id) : Results.BadRequest());
internalApi.MapPost("/application-services/{id:guid}/state", async (Guid id, ApplicationStateChange request, ApplicationLifecycle lifecycle, CancellationToken token) =>
    Results.Ok(await lifecycle.ChangeAsync(id, request, token)));
internalApi.MapGet("/application-services", async (int? page, ApplicationCatalog catalog, CancellationToken token) =>
    page is < 1 or > 1000 ? Results.BadRequest() : Results.Ok(await catalog.ListAsync(page ?? 1, token)));
internalApi.MapGet("/application-services/{id:guid}", async (Guid id, ApplicationCatalog catalog, CancellationToken token) =>
    await catalog.GetAsync(id, token) is { } service ? Results.Ok(service) : Results.NotFound());
internalApi.MapGet("/application-candidates", async (int? page, ApplicationDiscovery discovery, CancellationToken token) =>
    page is < 1 ? Results.BadRequest() : Results.Ok(await discovery.ListAsync(page ?? 1, token)));
internalApi.MapPost("/application-candidates/{id:guid}/approve", async (Guid id, ApplicationApprovalRequest request, ApplicationApproval approval, CancellationToken token) =>
    Results.Ok(await approval.ApproveAsync(id, request.ActorId, token)));
app.MapDeviceManagement();
app.MapAgentManagement();
app.MapPublicTemplateManagement();
app.Run();

public partial class Program;

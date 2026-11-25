using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Messaging;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Resources;
using Wolverine;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.Security;
using Vigiloom.UpdateSecurity.AgentPackages;
using Vigiloom.Modules.Resources.Application.AgentPackages;
using Vigiloom.Resources.AgentPackages;
using Vigiloom.Contracts.Agent;

var builder = WebApplication.CreateBuilder(args);
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
builder.Host.ConfigureServices((context, services) => services.AddWolverine(options =>
{
    var database = context.Configuration.GetConnectionString("Resources")
        ?? throw new InvalidOperationException("ConnectionStrings:Resources is required.");
    var broker = context.Configuration.GetConnectionString("Messaging")
        ?? throw new InvalidOperationException("ConnectionStrings:Messaging is required.");
    options.UseVigiloomMessaging(database, broker);
    options.Discovery.DisableConventionalDiscovery();
    options.PublishReliablyTo<NetworkDeviceChanged>(context.Configuration["Messaging:NetworkQueue"] ?? "vigiloom.network.resources");
    options.PublishReliablyTo<AgentDeviceChanged>(context.Configuration["Messaging:AgentQueue"] ?? "vigiloom.agent.resources");
    options.PublishReliablyTo<AgentAssignmentChanged>(context.Configuration["Messaging:AgentQueue"] ?? "vigiloom.agent.resources");
    options.PublishReliablyTo<AgentVersionAssignmentAudit>(context.Configuration["Messaging:AuditQueue"] ?? "vigiloom.operations.audit");
    options.PublishReliablyTo<GroupAuditRecorded>(context.Configuration["Messaging:AuditQueue"] ?? "vigiloom.operations.audit");
    options.PublishReliablyTo<ApplicationApprovalAudit>(context.Configuration["Messaging:AuditQueue"] ?? "vigiloom.operations.audit");
    options.PublishReliablyTo<ApplicationApproved>(context.Configuration["Messaging:ObservabilityQueue"] ?? "vigiloom.observability.catalog");
}));
builder.Services.AddModuleSqlServer<ResourcesDbContext>(
    _ => builder.Configuration.GetConnectionString("Resources")
        ?? throw new InvalidOperationException("ConnectionStrings:Resources is required."), "resources", pooled: true);
builder.Services.AddScoped<DevicePingSource>();
builder.Services.AddScoped<DeviceAgentSource>();
builder.Services.AddScoped<GroupMembersReader>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DeviceManagement>();
builder.Services.AddScoped<DeviceChangeOutbox>();
builder.Services.AddScoped<GroupManagement>();
builder.Services.AddScoped<GroupAuditOutbox>();
builder.Services.AddScoped<ServiceGroupManagement>();
builder.Services.AddScoped<ApplicationDiscovery>();
builder.Services.AddScoped<ApplicationApproval>();
builder.Services.AddScoped<SelfServiceRegistration>();
builder.Services.AddScoped<PackageCatalog>();
builder.Services.AddScoped<AgentAssignments>();
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
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<IApplicationSourceVerifier>(services => services.GetRequiredService<ObservationSourceVerifier>());

var app = builder.Build();
var internalApi = app.MapGroup("/internal/v1");
internalApi.MapGet("/ping-targets", (DevicePingSource source, CancellationToken token) => source.ReadEnabledAsync(token));
internalApi.MapGet("/devices/{id:guid}/agent", async (Guid id, DeviceAgentSource source, CancellationToken token) =>
    await source.GetAsync(id, token) is { } device ? Results.Ok(device) : Results.NotFound());
internalApi.MapGet("/groups/{id:guid}/members", async (Guid id, GroupMembersReader source, CancellationToken token) =>
    await source.GetAsync(id, token) is { } members ? Results.Ok(members) : Results.NotFound());
internalApi.MapInternalDeviceManagement();
internalApi.MapInternalGroupManagement();
internalApi.MapInternalPackages();
internalApi.MapPost("/application-candidates/observe", async (ApplicationObservation request, ApplicationDiscovery discovery, CancellationToken token) =>
    Results.Ok(await discovery.ObserveAsync(request, token)));
internalApi.MapPost("/self-services/register", async (ApplicationObservation request, SelfServiceRegistration registration, CancellationToken token) =>
    await registration.RegisterAsync(request, token) is { } id ? Results.Ok(id) : Results.BadRequest());
internalApi.MapGet("/application-candidates", async (int? page, ApplicationDiscovery discovery, CancellationToken token) =>
    page is < 1 ? Results.BadRequest() : Results.Ok(await discovery.ListAsync(page ?? 1, token)));
internalApi.MapPost("/application-candidates/{id:guid}/approve", async (Guid id, ApplicationApprovalRequest request, ApplicationApproval approval, CancellationToken token) =>
    Results.Ok(await approval.ApproveAsync(id, request.ActorId, token)));
app.MapDeviceManagement();
app.Run();

public partial class Program;

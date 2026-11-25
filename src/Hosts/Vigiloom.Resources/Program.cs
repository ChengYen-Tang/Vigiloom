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
    options.PublishReliablyTo<GroupAuditRecorded>(context.Configuration["Messaging:AuditQueue"] ?? "vigiloom.operations.audit");
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

var app = builder.Build();
var internalApi = app.MapGroup("/internal/v1");
internalApi.MapGet("/ping-targets", (DevicePingSource source, CancellationToken token) => source.ReadEnabledAsync(token));
internalApi.MapGet("/devices/{id:guid}/agent", async (Guid id, DeviceAgentSource source, CancellationToken token) =>
    await source.GetAsync(id, token) is { } device ? Results.Ok(device) : Results.NotFound());
internalApi.MapGet("/groups/{id:guid}/members", async (Guid id, GroupMembersReader source, CancellationToken token) =>
    await source.GetAsync(id, token) is { } members ? Results.Ok(members) : Results.NotFound());
internalApi.MapInternalDeviceManagement();
internalApi.MapInternalGroupManagement();
app.MapDeviceManagement();
app.Run();

public partial class Program;

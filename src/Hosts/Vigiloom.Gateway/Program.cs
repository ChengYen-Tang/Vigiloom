using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Gateway.Authentication;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddVigiloomDataProtection(builder.Configuration);
builder.Services.AddVigiloomIdentityCredentialClient();
builder.Services.AddVigiloomIdentityCookieClient();
builder.Services.AddOptions<IdentityOptions>();
builder.Services.AddAuthentication(options =>
{
    options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
    options.DefaultForbidScheme = IdentityConstants.ApplicationScheme;
}).AddIdentityCookies();
builder.Services.AddAuthentication().AddVigiloomApiCredentials()
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AgentAuthentication>(AgentAuthentication.SchemeName, _ => { });
builder.Services.AddHttpClient(AgentAuthentication.SchemeName, (services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Vigiloom:Gateway:AgentIdentityAddress"] ?? "http://vigiloom-agent";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options =>
{
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/account/access-denied";
    options.SlidingExpiration = false;
    options.Events.OnValidatePrincipal = async context =>
    {
        await context.HttpContext.RequestServices.GetRequiredService<IdentityCookieValidator>().ValidateAsync(context);
        context.ShouldRenew = false;
    };
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});
builder.Services.AddAuthorization(options =>
{
    options.AddVigiloomAccessPolicies();
    options.AddPolicy("GatewayAgent", policy => policy.AddAuthenticationSchemes(AgentAuthentication.SchemeName).RequireAuthenticatedUser());
    options.AddPolicy("GatewayAdministrator", policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
        .RequireAuthenticatedUser().RequireRole(WorkspaceAccess.Administrator));
    options.AddPolicy("DenyUnmappedDeviceMethod", policy => policy.RequireAssertion(_ => false));
});
builder.Services.AddServiceDiscovery();
builder.Services.ConfigureHttpClientDefaults(client => client.AddServiceDiscovery());
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddServiceDiscoveryDestinationResolver();

var app = builder.Build();
app.UseWebSockets();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/internal"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();
app.MapReverseProxy();
app.Run();

public partial class Program;

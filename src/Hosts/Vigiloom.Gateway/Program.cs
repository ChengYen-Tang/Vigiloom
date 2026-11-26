using Vigiloom.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Infrastructure.Security;
using Vigiloom.Gateway.Authentication;
using Vigiloom.Gateway.Telemetry;

var builder = WebApplication.CreateBuilder(args);
builder.AddVigiloomSelfTelemetry("Vigiloom.Gateway");
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
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AgentAuthentication>(AgentAuthentication.SchemeName, _ => { })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SourceAuthentication>(SourceAuthentication.SchemeName, _ => { });
builder.Services.AddHttpClient(AgentAuthentication.SchemeName, (services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Vigiloom:Gateway:AgentIdentityAddress"] ?? "http://vigiloom-agent";
    client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }).AddServiceDiscovery();
builder.Services.AddHttpClient(SourceAuthentication.SchemeName, (services, client) =>
{
    var address = services.GetRequiredService<IConfiguration>()["Vigiloom:Gateway:SourceIdentityAddress"] ?? "http://vigiloom-observability";
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
    options.AddPolicy("GatewaySource", policy => policy.AddAuthenticationSchemes(SourceAuthentication.SchemeName).RequireAuthenticatedUser());
    options.AddPolicy("GatewayAgent", policy => policy.AddAuthenticationSchemes(AgentAuthentication.SchemeName).RequireAuthenticatedUser());
    options.AddPolicy("AgentChannelHttps", policy => policy.RequireAssertion(context => context.Resource is HttpContext http && http.Request.IsHttps));
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
app.UseWhen(context => context.Request.Path.StartsWithSegments("/api/v1/otlp"), otlp =>
    otlp.Use(async (context, next) =>
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await OtlpErrors.WriteAsync(context, StatusCodes.Status503ServiceUnavailable);
            return;
        }
        if (!context.Response.HasStarted && context.Response.StatusCode >= 400 && context.Response.ContentLength is null)
            await OtlpErrors.WriteAsync(context, context.Response.StatusCode);
    }));
app.UseAuthentication();
app.UseAuthorization();
app.MapReverseProxy();
app.Run();

public partial class Program;

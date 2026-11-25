using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Infrastructure.Authentication;

namespace Vigiloom.Web.Authentication;

internal static class IdentityRegistration
{
    public static IServiceCollection AddVigiloomIdentity(this IServiceCollection services)
    {
        services.AddIdentityAccessOperations();
        services.AddScoped<TwoFactorChallenge>();
        services.AddScoped<AccountLogout>();
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = IdentityConstants.ApplicationScheme;
            options.DefaultAuthenticateScheme = PublicAssetAuthentication.SelectorScheme;
            options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
        }).AddIdentityCookies();
        services.AddAuthentication()
            .AddPolicyScheme(PublicAssetAuthentication.SelectorScheme, null, options =>
                options.ForwardDefaultSelector = context => PublicAssetAuthentication.IsPublicAsset(context.Request.Path)
                    ? PublicAssetAuthentication.AssetScheme : IdentityConstants.ApplicationScheme)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, PublicAssetAuthentication>(
                PublicAssetAuthentication.AssetScheme, _ => { })
            .AddVigiloomApiCredentials()
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, AgentCredentialHandler>(
                AgentCredentialHandler.SchemeName, _ => { })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ServiceSourceCredentialHandler>(
                ServiceSourceCredentialHandler.SchemeName, _ => { });
        services.AddAuthorization(options =>
        {
            options.AddVigiloomAccessPolicies();
            options.AddPolicy("Agent", policy => policy.AddAuthenticationSchemes(AgentCredentialHandler.SchemeName)
                .RequireAuthenticatedUser().RequireClaim("vigiloom:principal-type", "agent"));
            options.AddPolicy("ServiceSource", policy => policy.AddAuthenticationSchemes(ServiceSourceCredentialHandler.SchemeName)
                .RequireAuthenticatedUser().RequireClaim("vigiloom:principal-type", "service-source"));
        });
        services.AddIdentityCore<Account>().AddSignInManager<BoundSignInManager>();
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Events.OnValidatePrincipal = async context =>
            {
                var id = context.Principal?.FindFirstValue(ClaimTypes.Name);
                var stamp = context.Principal?.FindFirstValue(BoundSignInManager.StampClaim);
                if (!string.IsNullOrWhiteSpace(context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Services:Identity:Url"]))
                {
                    var validator = context.HttpContext.RequestServices.GetRequiredService<RemoteCookieValidator>();
                    if (!await validator.ValidateTwoFactorAsync(id, stamp, context.HttpContext.RequestAborted)) context.RejectPrincipal();
                    return;
                }
                var db = context.HttpContext.RequestServices.GetRequiredService<IdentityAccessDbContext>();
                var account = id is not null && Guid.TryParse(id, out var parsed)
                    ? await db.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == parsed) : null;
                if (account is null || stamp is null || account.SecurityStamp != stamp || !account.TwoFactorEnabled ||
                    (account.LockoutEnabled && account.LockoutEnd > DateTimeOffset.UtcNow)) context.RejectPrincipal();
            };
        });
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.LoginPath = "/account/login";
            options.AccessDeniedPath = "/account/access-denied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.Events.OnValidatePrincipal = async context =>
            {
                if (string.IsNullOrWhiteSpace(context.HttpContext.RequestServices.GetRequiredService<IConfiguration>()["Services:Identity:Url"]))
                    await SecurityStampValidator.ValidatePrincipalAsync(context);
                else
                    await context.HttpContext.RequestServices.GetRequiredService<IdentityCookieValidator>().ValidateAsync(context);
                if (context.Principal?.Identity is not ClaimsIdentity identity || context.Properties.ExpiresUtc is not { } expires) return;
                foreach (var claim in identity.FindAll(SessionLifetime.ExpirationClaim).ToArray()) identity.RemoveClaim(claim);
                identity.AddClaim(new Claim(SessionLifetime.ExpirationClaim, expires.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)));
            };
            options.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                else context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = StatusCodes.Status403Forbidden;
                else context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        });
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        return services;
    }
}

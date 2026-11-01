using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Authentication;

internal static class IdentityRegistration
{
    public static IServiceCollection AddVigiloomIdentity(this IServiceCollection services)
    {
        services.AddScoped<InitialAdministrator>();
        services.AddScoped<AccountAdministration>();
        services.AddScoped<AccountPasswordReset>();
        services.AddScoped<SelfPasswordChange>();
        services.AddScoped<TwoFactorManagement>();
        services.AddScoped<TwoFactorChallenge>();
        services.AddScoped<AccountLogout>();
        services.AddScoped<ApiCredentials>();
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
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiCredentialHandler>(
                ApiCredentialHandler.SchemeName, _ => { });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(AccessPolicies.Administrator, policy => policy.RequireRole(BuiltInRoles.Administrator));
            options.AddPolicy(AccessPolicies.MonitoringRead, policy => policy.RequireRole(BuiltInRoles.Administrator, BuiltInRoles.ReadOnly));
            options.AddPolicy("ApiClientRead", policy => policy.AddAuthenticationSchemes(ApiCredentialHandler.SchemeName)
                .RequireAuthenticatedUser().RequireClaim("scope", "monitoring.read"));
            options.AddPolicy("ApiClientWrite", policy => policy.AddAuthenticationSchemes(ApiCredentialHandler.SchemeName)
                .RequireAuthenticatedUser().RequireClaim("scope", "monitoring.write"));
            options.AddPolicy("DeviceRead", policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, ApiCredentialHandler.SchemeName)
                .RequireAuthenticatedUser().RequireAssertion(context => context.User.IsInRole(BuiltInRoles.Administrator) ||
                    context.User.IsInRole(BuiltInRoles.ReadOnly) || context.User.HasClaim("scope", "monitoring.read")));
            options.AddPolicy("DeviceWrite", policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, ApiCredentialHandler.SchemeName)
                .RequireAuthenticatedUser().RequireAssertion(context => context.User.IsInRole(BuiltInRoles.Administrator) ||
                    context.User.HasClaim("scope", "monitoring.write")));
        });
        services.AddIdentityCore<Account>(options =>
        {
            options.Password.RequiredLength = 12;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddRoles<Role>()
          .AddEntityFrameworkStores<IdentityAccessDbContext>()
          .AddSignInManager<BoundSignInManager>()
          .AddDefaultTokenProviders();
        services.AddScoped<IUserStore<Account>, ProtectedUserStore>();
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Events.OnValidatePrincipal = async context =>
            {
                var id = context.Principal?.FindFirstValue(ClaimTypes.Name);
                var stamp = context.Principal?.FindFirstValue(BoundSignInManager.StampClaim);
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
                await SecurityStampValidator.ValidatePrincipalAsync(context);
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
        services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = TimeSpan.FromMinutes(15));
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        return services;
    }
}

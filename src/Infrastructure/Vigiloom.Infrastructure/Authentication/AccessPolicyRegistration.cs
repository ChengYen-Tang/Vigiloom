using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Vigiloom.Contracts.Workspace;

namespace Vigiloom.Infrastructure.Authentication;

public static class AccessPolicyRegistration
{
    public static AuthenticationBuilder AddVigiloomApiCredentials(this AuthenticationBuilder builder) =>
        builder.AddScheme<AuthenticationSchemeOptions, ApiCredentialHandler>(ApiCredentialHandler.SchemeName, _ => { });

    public static void AddVigiloomAccessPolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(WorkspaceAccess.Administrator, policy => policy.RequireRole(WorkspaceAccess.Administrator));
        options.AddPolicy(WorkspaceAccess.MonitoringRead, policy => policy.RequireRole(WorkspaceAccess.Administrator, WorkspaceAccess.ReadOnly));
        options.AddPolicy("ApiClientRead", policy => policy.AddAuthenticationSchemes(ApiCredentialHandler.SchemeName)
            .RequireAuthenticatedUser().RequireClaim("scope", "monitoring.read"));
        options.AddPolicy("ApiClientWrite", policy => policy.AddAuthenticationSchemes(ApiCredentialHandler.SchemeName)
            .RequireAuthenticatedUser().RequireClaim("scope", "monitoring.write"));
        options.AddPolicy("MonitoringApiRead", policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, ApiCredentialHandler.SchemeName)
            .RequireAuthenticatedUser().RequireAssertion(context => context.User.IsInRole(WorkspaceAccess.Administrator) ||
                context.User.IsInRole(WorkspaceAccess.ReadOnly) || context.User.HasClaim("scope", "monitoring.read")));
        options.AddPolicy("DeviceWrite", policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, ApiCredentialHandler.SchemeName)
            .RequireAuthenticatedUser().RequireAssertion(context => context.User.IsInRole(WorkspaceAccess.Administrator) ||
                context.User.HasClaim("scope", "monitoring.write")));
    }
}

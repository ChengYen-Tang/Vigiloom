using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Domain;

namespace Vigiloom.Web.Authentication;

// This is a server-side AuthenticationStateProvider that revalidates the security stamp for the connected user
// every minute an interactive circuit is connected.
internal sealed class IdentityRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
        => await RevalidateAsync(authenticationState.User, cancellationToken);

    internal async Task<bool> RevalidateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!SessionLifetime.IsCurrent(principal)) return false;
        // Get the user manager from a new scope to ensure it fetches fresh data
        await using var scope = scopeFactory.CreateAsyncScope();
        if (!string.IsNullOrWhiteSpace(scope.ServiceProvider.GetRequiredService<IConfiguration>()["Services:Identity:Url"]))
            return await scope.ServiceProvider.GetRequiredService<RemoteWorkspaceIdentity>().ValidateAsync(principal, cancellationToken, allowLockedOut: true) is not null;
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
        return await ValidateSecurityStampAsync(userManager, principal);
    }

    private async Task<bool> ValidateSecurityStampAsync(UserManager<Account> userManager, ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return false;
        }
        else if (!userManager.SupportsUserSecurityStamp)
        {
            return true;
        }
        else
        {
            var principalStamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
            var userStamp = await userManager.GetSecurityStampAsync(user);
            return principalStamp == userStamp;
        }
    }
}

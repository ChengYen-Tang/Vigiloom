using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Application;

namespace Vigiloom.Web.Authentication;

internal static class IdentityActorFactory
{
    public static IdentityActor Create(ClaimsPrincipal principal, IdentityOptions options)
    {
        if (principal.HasClaim(claim => claim.Type == SessionLifetime.ExpirationClaim) && !SessionLifetime.IsCurrent(principal))
            throw new UnauthorizedAccessException();
        var id = principal.FindFirstValue(options.ClaimsIdentity.UserIdClaimType);
        var stamp = principal.FindFirstValue(options.ClaimsIdentity.SecurityStampClaimType);
        if (!Guid.TryParse(id, out var accountId) || string.IsNullOrEmpty(stamp)) throw new UnauthorizedAccessException();
        return new IdentityActor(accountId, stamp);
    }
}

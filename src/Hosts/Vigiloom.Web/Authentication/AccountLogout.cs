using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Vigiloom.Web.Authentication;

public sealed class AccountLogout(IServiceProvider services, IConfiguration configuration, IOptions<IdentityOptions> options)
{
    public async Task<bool> RevokeSessionsAsync(ClaimsPrincipal principal, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(configuration["Services:Identity:Url"])) return await services.GetRequiredService<AccountSessionRevocation>().RevokeAsync(principal, token);
        if (principal.HasClaim(claim => claim.Type == SessionLifetime.ExpirationClaim) && !SessionLifetime.IsCurrent(principal)) return true;
        var id = principal.FindFirstValue(options.Value.ClaimsIdentity.UserIdClaimType);
        var stamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        return await services.GetRequiredService<RemoteCookieValidator>().RevokeSessionsAsync(id, stamp, token);
    }
}

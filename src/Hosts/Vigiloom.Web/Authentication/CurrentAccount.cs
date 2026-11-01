using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Domain;

namespace Vigiloom.Web.Authentication;

internal static class CurrentAccount
{
    public static async Task<Account?> FindValidAsync(UserManager<Account> users, ClaimsPrincipal principal, IOptions<IdentityOptions> options)
    {
        var id = users.GetUserId(principal);
        var stamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        if (id is null || stamp is null) return null;
        var account = await users.FindByIdAsync(id);
        if (account is null || stamp != await users.GetSecurityStampAsync(account) || await users.IsLockedOutAsync(account)) return null;
        return account;
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Domain;

namespace Vigiloom.Web.Authentication;

internal static class CurrentAccount
{
    public static async Task<Account?> FindValidAsync(IdentityAccessDbContext db, UserManager<Account> users, ClaimsPrincipal principal, IOptions<IdentityOptions> options, bool allowLockedOut = false)
    {
        var id = users.GetUserId(principal);
        var stamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        if (id is null || stamp is null) return null;
        var account = await users.FindByIdAsync(id);
        if (account is null) return null;
        // Cookie validation may have tracked an older copy before this request obtained the mutation lock.
        await db.Entry(account).ReloadAsync();
        if (db.Entry(account).State == EntityState.Detached || stamp != await users.GetSecurityStampAsync(account) ||
            (!allowLockedOut && await users.IsLockedOutAsync(account))) return null;
        return account;
    }
}

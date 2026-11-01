using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Authentication;

public sealed class AccountLogout(IdentityAccessDbContext db, UserManager<Account> users, IOptions<IdentityOptions> options)
{
    public async Task<bool> RevokeSessionsAsync(ClaimsPrincipal principal, CancellationToken token)
    {
        await using var transaction = await AccountMutationLock.AcquireAsync(db, token);
        var account = await CurrentAccount.FindValidAsync(db, users, principal, options, allowLockedOut: true);
        if (account is not null && !(await users.UpdateSecurityStampAsync(account)).Succeeded) return false;
        await transaction.CommitAsync(token);
        return true;
    }
}

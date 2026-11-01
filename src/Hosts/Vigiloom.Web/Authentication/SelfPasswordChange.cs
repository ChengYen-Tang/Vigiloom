using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Authentication;

public sealed class SelfPasswordChange(IdentityAccessDbContext db, UserManager<Account> users, IOptions<IdentityOptions> options)
{
    public async Task<AccountOperationResult> ChangeAsync(ClaimsPrincipal principal, string currentPassword, string newPassword,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await AccountMutationLock.AcquireAsync(db, cancellationToken);
        var account = await CurrentAccount.FindValidAsync(users, principal, options)
            ?? throw new UnauthorizedAccessException("The account session is no longer valid.");
        var changed = await users.ChangePasswordAsync(account, currentPassword, newPassword);
        if (!changed.Succeeded) return AccountOperationResult.Failed(changed);
        db.AccountAuditEvents.Add(new AccountAuditEvent(account.Id, account.Id, "PasswordChanged", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AccountOperationResult.Success;
    }
}

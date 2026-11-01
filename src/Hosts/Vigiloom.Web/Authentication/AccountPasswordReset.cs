using Microsoft.AspNetCore.Identity;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Authentication;

public sealed class AccountPasswordReset(IdentityAccessDbContext db, UserManager<Account> users)
{
    public async Task<bool> RedeemAsync(string userName, string code, string newPassword, CancellationToken cancellationToken = default)
    {
        var account = await users.FindByNameAsync(userName);
        if (account is null || !await users.VerifyUserTokenAsync(account, users.Options.Tokens.PasswordResetTokenProvider,
                UserManager<Account>.ResetPasswordTokenPurpose, code)) return false;
        await using var transaction = await AccountMutationLock.AcquireAsync(db, cancellationToken);
        await db.Entry(account).ReloadAsync(cancellationToken);
        if (db.Entry(account).State == Microsoft.EntityFrameworkCore.EntityState.Detached) return false;
        var changed = await users.ResetPasswordAsync(account, code, newPassword);
        if (!changed.Succeeded) return false;
        db.AccountAuditEvents.Add(new AccountAuditEvent(null, account.Id, "PasswordResetCompleted", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}

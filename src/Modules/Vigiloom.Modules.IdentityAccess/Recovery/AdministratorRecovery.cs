using Microsoft.AspNetCore.Identity;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Modules.IdentityAccess.Recovery;

/// <summary>Called only by the locally privileged recovery command; never expose as a remote endpoint.</summary>
public sealed class AdministratorRecovery(IdentityAccessDbContext db, UserManager<Account> users)
{
    public async Task<string?> IssueCodeAsync(string userName, CancellationToken cancellationToken = default)
    {
        await using var transaction = await AccountMutationLock.AcquireAsync(db, cancellationToken);
        var account = await users.FindByNameAsync(userName);
        if (account is null || !await users.IsInRoleAsync(account, BuiltInRoles.Administrator)) return null;
        if (await users.HasPasswordAsync(account)) EnsureSuccess(await users.RemovePasswordAsync(account));
        if (await users.GetTwoFactorEnabledAsync(account))
        {
            EnsureSuccess(await users.SetTwoFactorEnabledAsync(account, false));
            EnsureSuccess(await users.RemoveAuthenticationTokenAsync(account, ProtectedUserStore.InternalProvider, ProtectedUserStore.AuthenticatorKeyName));
            _ = await users.GenerateNewTwoFactorRecoveryCodesAsync(account, 0);
        }
        if (await users.IsLockedOutAsync(account)) EnsureSuccess(await users.SetLockoutEndDateAsync(account, null));
        EnsureSuccess(await users.UpdateSecurityStampAsync(account));
        var code = await users.GeneratePasswordResetTokenAsync(account);
        db.AccountAuditEvents.Add(new AccountAuditEvent(null, account.Id, "LocalAdministratorRecovery", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return code;
    }

    private static void EnsureSuccess(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Administrator recovery could not update the account.");
    }
}

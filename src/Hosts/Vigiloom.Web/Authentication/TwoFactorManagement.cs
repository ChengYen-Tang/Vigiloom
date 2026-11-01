using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Authentication;

public sealed class TwoFactorManagement(IdentityAccessDbContext db, UserManager<Account> users, IOptions<IdentityOptions> options)
{
    public async Task<TwoFactorResult> BeginAsync(ClaimsPrincipal principal, string password, CancellationToken token = default)
    {
        await using var transaction = await AccountMutationLock.AcquireAsync(db, token);
        var account = await CurrentAccount.FindValidAsync(db, users, principal, options)
            ?? throw new UnauthorizedAccessException("The account session is no longer valid.");
        if (await users.GetTwoFactorEnabledAsync(account)) return new(TwoFactorStatus.AlreadyEnabled, null, []);
        if (!await users.CheckPasswordAsync(account, password)) return new(TwoFactorStatus.InvalidCredentials, null, []);
        var key = await users.GetAuthenticatorKeyAsync(account);
        if (key is null)
        {
            EnsureSuccess(await users.ResetAuthenticatorKeyAsync(account));
            key = await users.GetAuthenticatorKeyAsync(account);
            db.AccountAuditEvents.Add(new AccountAuditEvent(account.Id, account.Id, "TwoFactorEnrollmentStarted", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return new(TwoFactorStatus.Success, key, []);
    }

    public async Task<TwoFactorResult> EnableAsync(ClaimsPrincipal principal, string code, CancellationToken token = default)
    {
        await using var transaction = await AccountMutationLock.AcquireAsync(db, token);
        var account = await CurrentAccount.FindValidAsync(db, users, principal, options)
            ?? throw new UnauthorizedAccessException("The account session is no longer valid.");
        if (await users.GetTwoFactorEnabledAsync(account)) return new(TwoFactorStatus.AlreadyEnabled, null, []);
        if (!await users.VerifyTwoFactorTokenAsync(account, TokenOptions.DefaultAuthenticatorProvider, code))
            return new(TwoFactorStatus.InvalidCredentials, null, []);
        EnsureSuccess(await users.SetTwoFactorEnabledAsync(account, true));
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(account, 10))?.ToArray()
            ?? throw new InvalidOperationException("Recovery code storage is unavailable.");
        EnsureSuccess(await users.UpdateSecurityStampAsync(account));
        db.AccountAuditEvents.Add(new AccountAuditEvent(account.Id, account.Id, "TwoFactorEnabled", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(TwoFactorStatus.Success, null, codes);
    }

    public async Task<TwoFactorResult> DisableAsync(ClaimsPrincipal principal, string password, string code, CancellationToken token = default)
    {
        await using var transaction = await AccountMutationLock.AcquireAsync(db, token);
        var account = await CurrentAccount.FindValidAsync(db, users, principal, options)
            ?? throw new UnauthorizedAccessException("The account session is no longer valid.");
        if (!await users.GetTwoFactorEnabledAsync(account)) return new(TwoFactorStatus.NotEnabled, null, []);
        if (!await users.CheckPasswordAsync(account, password)) return new(TwoFactorStatus.InvalidCredentials, null, []);
        var valid = await users.VerifyTwoFactorTokenAsync(account, TokenOptions.DefaultAuthenticatorProvider, code);
        if (!valid) valid = (await users.RedeemTwoFactorRecoveryCodeAsync(account, code)).Succeeded;
        if (!valid) return new(TwoFactorStatus.InvalidCredentials, null, []);
        EnsureSuccess(await users.SetTwoFactorEnabledAsync(account, false));
        EnsureSuccess(await users.RemoveAuthenticationTokenAsync(account, ProtectedUserStore.InternalProvider, ProtectedUserStore.AuthenticatorKeyName));
        _ = await users.GenerateNewTwoFactorRecoveryCodesAsync(account, 0);
        EnsureSuccess(await users.UpdateSecurityStampAsync(account));
        db.AccountAuditEvents.Add(new AccountAuditEvent(account.Id, account.Id, "TwoFactorDisabled", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(TwoFactorStatus.Success, null, []);
    }

    private static void EnsureSuccess(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Unable to update two-factor authentication.");
    }
}

public enum TwoFactorStatus { Success, InvalidCredentials, AlreadyEnabled, NotEnabled }
public sealed record TwoFactorResult(TwoFactorStatus Status, string? Key, IReadOnlyList<string> RecoveryCodes);

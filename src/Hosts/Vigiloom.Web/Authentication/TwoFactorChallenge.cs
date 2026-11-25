using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Web.Authentication;

internal sealed class TwoFactorChallenge(IdentityAccessDbContext db, UserManager<Account> users, SignInManager<Account> signIn,
    IConfiguration configuration, IServiceProvider services)
{
    public async Task<bool> HasPendingAsync(HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(configuration["Services:Identity:Url"]))
            return await signIn.GetTwoFactorAuthenticationUserAsync() is not null;
        return (await context.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme)).Succeeded;
    }

    public async Task<SignInResult> CompleteAsync(HttpContext context, string code, bool recovery, bool persistent,
        CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(configuration["Services:Identity:Url"]))
            return await services.GetRequiredService<RemotePasswordLogin>().CompleteTwoFactorAsync(context, code, recovery, persistent, token);
        await using var transaction = await AccountMutationLock.AcquireAsync(db, token);
        var challenge = await context.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
        var id = challenge.Principal?.FindFirstValue(ClaimTypes.Name);
        var stamp = challenge.Principal?.FindFirstValue(BoundSignInManager.StampClaim);
        var account = id is null ? null : await users.FindByIdAsync(id);
        if (account is not null) await db.Entry(account).ReloadAsync(token);
        if (account is null || db.Entry(account).State == EntityState.Detached || stamp is null ||
            stamp != await users.GetSecurityStampAsync(account) || !await users.GetTwoFactorEnabledAsync(account) ||
            await users.IsLockedOutAsync(account))
        {
            await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
            return SignInResult.Failed;
        }

        var result = recovery
            ? await signIn.TwoFactorRecoveryCodeSignInAsync(code)
            : await signIn.TwoFactorAuthenticatorSignInAsync(code, persistent, rememberClient: false);
        // Identity's recovery-code flow does not count failed attempts or enforce lockout itself.
        if (recovery && !result.Succeeded) await users.AccessFailedAsync(account);
        if (recovery && result.Succeeded && persistent)
            await signIn.SignInWithClaimsAsync(account, new AuthenticationProperties { IsPersistent = true },
                [new Claim("amr", "mfa")]);
        await transaction.CommitAsync(token);
        return result;
    }
}

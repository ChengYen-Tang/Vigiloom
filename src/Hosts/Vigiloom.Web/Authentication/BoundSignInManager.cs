using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Domain;

namespace Vigiloom.Web.Authentication;

internal sealed class BoundSignInManager(
    UserManager<Account> users, IHttpContextAccessor context, IUserClaimsPrincipalFactory<Account> claims,
    IOptions<IdentityOptions> options, ILogger<SignInManager<Account>> logger,
    IAuthenticationSchemeProvider schemes, IUserConfirmation<Account> confirmation)
    : SignInManager<Account>(users, context, claims, options, logger, schemes, confirmation)
{
    internal const string StampClaim = "Vigiloom.TwoFactorSecurityStamp";

    protected override async Task<SignInResult> SignInOrTwoFactorAsync(
        Account user, bool isPersistent, string? loginProvider = null, bool bypassTwoFactor = false)
    {
        // Capture the stamp associated with the password that was just verified.
        var stamp = await UserManager.GetSecurityStampAsync(user);
        var result = await base.SignInOrTwoFactorAsync(user, isPersistent, loginProvider, bypassTwoFactor);
        if (result.RequiresTwoFactor)
        {
            var identity = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
            identity.AddClaim(new Claim(ClaimTypes.Name, await UserManager.GetUserIdAsync(user)));
            identity.AddClaim(new Claim(StampClaim, stamp));
            if (loginProvider is not null) identity.AddClaim(new Claim(ClaimTypes.AuthenticationMethod, loginProvider));
            await Context.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(identity));
        }
        return result;
    }
}

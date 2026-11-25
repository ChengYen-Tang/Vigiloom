using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Domain;

namespace Vigiloom.Web.Authentication;

internal sealed class AccountLogin(IServiceProvider services, IConfiguration configuration)
{
    public Task<SignInResult> PasswordAsync(HttpContext context, string userName, string password, bool remember, CancellationToken token) =>
        string.IsNullOrWhiteSpace(configuration["Services:Identity:Url"])
            ? services.GetRequiredService<SignInManager<Account>>().PasswordSignInAsync(userName, password, remember, lockoutOnFailure: true)
            : services.GetRequiredService<RemotePasswordLogin>().PasswordAsync(context, userName, password, remember, token);
}

internal sealed class RemotePasswordLogin(HttpClient client, IOptions<IdentityOptions> options)
{
    public async Task<SignInResult> CompleteTwoFactorAsync(HttpContext context, string code, bool recovery, bool persistent, CancellationToken token)
    {
        var challenge = await context.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
        var id = challenge.Principal?.FindFirstValue(ClaimTypes.Name);
        var stamp = challenge.Principal?.FindFirstValue(BoundSignInManager.StampClaim);
        if (!Guid.TryParse(id, out var accountId) || string.IsNullOrEmpty(stamp)) return SignInResult.Failed;
        using var response = await client.PostAsJsonAsync("internal/identity/two-factor/complete",
            new CompleteTwoFactorRequest(accountId, stamp, code, recovery), token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
            return SignInResult.Failed;
        }
        if (response.StatusCode == HttpStatusCode.Conflict) return SignInResult.Failed;
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ValidatedCookieResponse>(cancellationToken: token)
            ?? throw new InvalidOperationException("Identity service returned an empty two-factor response.");
        var claims = result.Claims.Select(claim => new Claim(claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer)).ToList();
        claims.Add(new Claim("amr", "mfa"));
        var identity = new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme,
            options.Value.ClaimsIdentity.UserNameClaimType, options.Value.ClaimsIdentity.RoleClaimType);
        await context.SignOutAsync(IdentityConstants.ExternalScheme);
        await context.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
        await context.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = persistent });
        return SignInResult.Success;
    }

    public async Task<SignInResult> PasswordAsync(HttpContext context, string userName, string password, bool remember, CancellationToken token)
    {
        using var response = await client.PostAsJsonAsync("internal/identity/password-login", new PasswordLoginRequest(userName, password), token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return SignInResult.Failed;
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<PasswordLoginResponse>(cancellationToken: token)
            ?? throw new InvalidOperationException("Identity service returned an empty login response.");
        if (login.RequiresTwoFactor)
        {
            var challenge = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
            challenge.AddClaim(new Claim(ClaimTypes.Name, login.AccountId.ToString("D")));
            challenge.AddClaim(new Claim(BoundSignInManager.StampClaim, login.SecurityStamp));
            await context.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(challenge));
            return SignInResult.TwoFactorRequired;
        }
        var claims = login.Claims.Select(claim => new Claim(claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer)).ToList();
        claims.Add(new Claim("amr", "pwd"));
        var identity = new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme,
            options.Value.ClaimsIdentity.UserNameClaimType, options.Value.ClaimsIdentity.RoleClaimType);
        await context.SignInAsync(IdentityConstants.ApplicationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = remember });
        return SignInResult.Success;
    }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Contracts.Identity;

namespace Vigiloom.Identity;

internal static class IdentitySessionEndpoints
{
    public static void MapIdentitySessionEndpoints(this WebApplication app)
    {
        // Internal only: the caller must independently check its session expiration.
        app.MapPost("/internal/identity/sessions/validate", async (
            ValidateSessionRequest request, HttpContext context, IdentityAccessDbContext db,
            UserManager<Account> users, IOptions<IdentityOptions> options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (request.AccountId == Guid.Empty || string.IsNullOrEmpty(request.SecurityStamp))
                return Results.Unauthorized();
            var claims = new ClaimsIdentity([
                new Claim(options.Value.ClaimsIdentity.UserIdClaimType, request.AccountId.ToString("D")),
                new Claim(options.Value.ClaimsIdentity.SecurityStampClaimType, request.SecurityStamp)
            ]);
            var account = await CurrentAccount.FindValidAsync(db, users, new ClaimsPrincipal(claims), options, request.AllowLockedOut);
            if (account is null) return Results.Unauthorized();
            var roles = await users.GetRolesAsync(account);
            return Results.Ok(new ValidatedSessionResponse(account.Id, account.UserName ?? "", roles.ToArray()));
        });
        app.MapPost("/internal/identity/cookies/validate", async (
            ValidateSessionRequest request, HttpContext context, IdentityAccessDbContext db,
            UserManager<Account> users, IOptions<IdentityOptions> options, IUserClaimsPrincipalFactory<Account> factory) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (request.AccountId == Guid.Empty || string.IsNullOrEmpty(request.SecurityStamp))
                return Results.Unauthorized();
            var identity = new ClaimsIdentity([
                new Claim(options.Value.ClaimsIdentity.UserIdClaimType, request.AccountId.ToString("D")),
                new Claim(options.Value.ClaimsIdentity.SecurityStampClaimType, request.SecurityStamp)
            ]);
            var account = await CurrentAccount.FindValidAsync(db, users, new ClaimsPrincipal(identity), options, allowLockedOut: true);
            if (account is null) return Results.Unauthorized();
            var principal = await factory.CreateAsync(account);
            return Results.Ok(new ValidatedCookieResponse(principal.Claims.Select(claim =>
                new IdentityClaim(claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer)).ToArray()));
        });
        app.MapPost("/internal/identity/two-factor/validate", async (
            ValidateSessionRequest request, HttpContext context, IdentityAccessDbContext db,
            UserManager<Account> users, IOptions<IdentityOptions> options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (request.AccountId == Guid.Empty || string.IsNullOrEmpty(request.SecurityStamp))
                return Results.Unauthorized();
            var identity = new ClaimsIdentity([
                new Claim(options.Value.ClaimsIdentity.UserIdClaimType, request.AccountId.ToString("D")),
                new Claim(options.Value.ClaimsIdentity.SecurityStampClaimType, request.SecurityStamp)
            ]);
            var account = await CurrentAccount.FindValidAsync(db, users, new ClaimsPrincipal(identity), options);
            return account is not null && await users.GetTwoFactorEnabledAsync(account)
                ? Results.NoContent() : Results.Unauthorized();
        });
        app.MapPost("/internal/identity/sessions/revoke", async (
            ValidateSessionRequest request, HttpContext context, IOptions<IdentityOptions> options, AccountSessionRevocation revocation, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (request.AccountId == Guid.Empty || string.IsNullOrEmpty(request.SecurityStamp)) return Results.NoContent();
            var identity = new ClaimsIdentity([
                new Claim(options.Value.ClaimsIdentity.UserIdClaimType, request.AccountId.ToString("D")),
                new Claim(options.Value.ClaimsIdentity.SecurityStampClaimType, request.SecurityStamp)
            ]);
            return await revocation.RevokeAsync(new ClaimsPrincipal(identity), token) ? Results.NoContent() : Results.Conflict();
        });
        app.MapPost("/internal/identity/password-login", async (
            PasswordLoginRequest request, HttpContext context, IdentityAccessDbContext db,
            UserManager<Account> users, SignInManager<Account> signIn, IUserClaimsPrincipalFactory<Account> factory, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await using var transaction = await AccountMutationLock.AcquireAsync(db, token);
            var account = await users.FindByNameAsync(request.UserName);
            if (account is null)
            {
                await transaction.CommitAsync(token);
                return Results.Unauthorized();
            }
            var result = await signIn.CheckPasswordSignInAsync(account, request.Password, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                await transaction.CommitAsync(token);
                return Results.Unauthorized();
            }
            var stamp = await users.GetSecurityStampAsync(account);
            var twoFactor = await signIn.IsTwoFactorEnabledAsync(account);
            var claims = twoFactor ? [] : (await factory.CreateAsync(account)).Claims.Select(claim =>
                new IdentityClaim(claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer)).ToArray();
            await transaction.CommitAsync(token);
            return Results.Ok(new PasswordLoginResponse(account.Id, stamp, twoFactor, claims));
        });
    }
}

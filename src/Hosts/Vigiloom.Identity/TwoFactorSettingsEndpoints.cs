using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Identity;

internal static class TwoFactorSettingsEndpoints
{
    public static void MapTwoFactorSettingsEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/identity/two-factor/settings/{action}", async (string action, TwoFactorSettingsRequest request,
            HttpContext context, AccountActorValidator actors, TwoFactorManagement settings, IdentityAccessDbContext db,
            UserManager<Account> users, IUserClaimsPrincipalFactory<Account> factory, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = await actors.FindAsync(request.Actor);
            if (actor is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (action == "state")
            {
                var account = await users.FindByIdAsync(request.Actor.AccountId.ToString("D"));
                if (account is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
                var enabled = await users.GetTwoFactorEnabledAsync(account);
                return Results.Json(new TwoFactorSettingsState(enabled, !enabled && await users.GetAuthenticatorKeyAsync(account) is not null));
            }
            var result = action switch
            {
                "begin" => await settings.BeginAsync(actor, request.Password ?? "", token),
                "enable" => await settings.EnableAsync(actor, request.Code ?? "", token),
                "disable" => await settings.DisableAsync(actor, request.Password ?? "", request.Code ?? "", token),
                _ => null
            };
            if (result is null) return Results.NotFound();
            IdentityClaim[]? claims = null;
            if (result.Status == TwoFactorStatus.Success)
            {
                var account = await users.FindByIdAsync(request.Actor.AccountId.ToString("D"));
                if (account is not null) await db.Entry(account).ReloadAsync(token);
                if (account is null || db.Entry(account).State == Microsoft.EntityFrameworkCore.EntityState.Detached ||
                    result.SecurityStamp != await users.GetSecurityStampAsync(account)) return Results.StatusCode(StatusCodes.Status403Forbidden);
                claims = (await factory.CreateAsync(account)).Claims.Select(claim =>
                    new IdentityClaim(claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer)).ToArray();
            }
            return Results.Json(new TwoFactorSettingsResponse((int)result.Status, result.Key, result.RecoveryCodes.ToArray(), claims));
        });
    }
}

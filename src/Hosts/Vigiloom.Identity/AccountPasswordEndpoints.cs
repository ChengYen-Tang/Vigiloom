using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Identity;

internal static class AccountPasswordEndpoints
{
    public static void MapAccountPasswordEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/identity/accounts/initialize", async (InitialAdministratorRequest request, HttpContext context,
            InitialAdministrator setup, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await setup.CreateAsync(request.SetupToken, request.UserName, request.Password, token));
        });
        app.MapPost("/internal/identity/accounts/redeem-password", async (RedeemPasswordRequest request, HttpContext context,
            AccountPasswordReset reset, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await reset.RedeemAsync(request.UserName, request.Code, request.Password, token));
        });
        app.MapPost("/internal/identity/accounts/change-password", async (ChangePasswordRequest request, HttpContext context,
            AccountActorValidator actors, SelfPasswordChange change, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = await actors.FindAsync(request.Actor);
            if (actor is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await change.ChangeAsync(actor, request.CurrentPassword, request.NewPassword, token));
        });
    }
}

internal sealed class AccountActorValidator(IdentityAccessDbContext db, UserManager<Account> users,
    IOptions<IdentityOptions> options, IUserClaimsPrincipalFactory<Account> factory)
{
    public async Task<ClaimsPrincipal?> FindAsync(IdentityActor actor)
    {
        if (actor.AccountId == Guid.Empty || string.IsNullOrEmpty(actor.SecurityStamp)) return null;
        var identity = new ClaimsIdentity([
            new Claim(options.Value.ClaimsIdentity.UserIdClaimType, actor.AccountId.ToString("D")),
            new Claim(options.Value.ClaimsIdentity.SecurityStampClaimType, actor.SecurityStamp)
        ]);
        var account = await CurrentAccount.FindValidAsync(db, users, new ClaimsPrincipal(identity), options);
        return account is null ? null : await factory.CreateAsync(account);
    }
}

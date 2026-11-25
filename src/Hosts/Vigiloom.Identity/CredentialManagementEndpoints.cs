using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Identity;

internal static class CredentialManagementEndpoints
{
    public static void MapCredentialManagementEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/identity/credentials/list", async (CredentialListRequest request, HttpContext context,
            CredentialActorValidator actors, ApiCredentials credentials, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = await actors.FindAsync(request.Actor);
            if (actor is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await credentials.ListAsync(actor, request.Page, token));
        });
        app.MapPost("/internal/identity/credentials/issue", async (CredentialIssueRequest request, HttpContext context,
            CredentialActorValidator actors, ApiCredentials credentials, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = await actors.FindAsync(request.Actor);
            if (actor is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await credentials.IssueAsync(actor, request.Name, request.CanWrite, request.Days, request.Password, token));
        });
        app.MapPost("/internal/identity/credentials/revoke", async (CredentialRevokeRequest request, HttpContext context,
            CredentialActorValidator actors, ApiCredentials credentials, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = await actors.FindAsync(request.Actor);
            if (actor is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(await credentials.RevokeAsync(actor, request.Id, request.Password, token));
        });
    }
}

internal sealed class CredentialActorValidator(IdentityAccessDbContext db, UserManager<Account> users,
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
        return account is not null && await users.IsInRoleAsync(account, BuiltInRoles.Administrator)
            ? await factory.CreateAsync(account) : null;
    }
}

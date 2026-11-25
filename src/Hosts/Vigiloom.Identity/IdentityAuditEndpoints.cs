using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Identity;

internal static class IdentityAuditEndpoints
{
    public static void MapIdentityAuditEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/identity/audit/query", async (IdentityAuditQueryRequest request, HttpContext context,
            AdministratorActorValidator actors, IdentityAuditFeed feed, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (await actors.FindAsync(request.Actor) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request.Source is not (AuditFeedSource.Accounts or AuditFeedSource.ApiCredentials) || !AuditFeedLimits.Valid(request.Query)) return Results.BadRequest();
            return Results.Ok(await feed.QueryAsync(request.Source, request.Query, token));
        });
        app.MapPost("/internal/identity/audit/names", async (IdentityAuditNamesRequest request, HttpContext context,
            AdministratorActorValidator actors, IdentityAccessDbContext db, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (await actors.FindAsync(request.Actor) is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (request.Ids.Length > AuditFeedLimits.PageSize) return Results.BadRequest();
            var names = await db.Users.AsNoTracking().Where(user => request.Ids.Contains(user.Id) && user.UserName != null)
                .ToDictionaryAsync(user => user.Id, user => user.UserName!, token);
            return Results.Ok(names);
        });
    }
}

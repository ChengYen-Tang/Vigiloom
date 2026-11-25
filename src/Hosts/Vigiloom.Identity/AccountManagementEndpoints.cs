using Vigiloom.Contracts.Identity;
using Vigiloom.Modules.IdentityAccess.Application;

namespace Vigiloom.Identity;

internal static class AccountManagementEndpoints
{
    public static void MapAccountManagementEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/identity/accounts/{action}", async (string action, AccountManagementRequest request,
            HttpContext context, AdministratorActorValidator actors, AccountAdministration accounts, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var actor = await actors.FindAsync(request.Actor);
            if (actor is null) return Results.StatusCode(StatusCodes.Status403Forbidden);
            object? result = action switch
            {
                "list" => await accounts.ListAsync(actor, request.Page, token),
                "get" => await accounts.GetAsync(actor, request.Id, token),
                "create" => await accounts.CreateAsync(actor, request.UserName ?? "", request.Role ?? "", token),
                "rename" => await accounts.RenameAsync(actor, request.Id, request.UserName ?? "", token),
                "role" => await accounts.SetRoleAsync(actor, request.Id, request.Role ?? "", token),
                "delete" => await accounts.DeleteAsync(actor, request.Id, token),
                "reset-password" => await accounts.IssuePasswordResetAsync(actor, request.Id, token),
                "reset-two-factor" => await accounts.ResetTwoFactorAsync(actor, request.Id, request.Password ?? "", token),
                _ => null
            };
            return result is null ? Results.NotFound() : Results.Json(result);
        });
    }
}

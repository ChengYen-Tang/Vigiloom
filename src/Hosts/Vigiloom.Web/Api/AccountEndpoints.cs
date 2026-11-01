using Microsoft.AspNetCore.Antiforgery;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/session/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new AntiforgeryResponse(antiforgery.GetAndStoreTokens(context).RequestToken!));
        }).RequireAuthorization().ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithSummary("Get an antiforgery token for cookie-authenticated API writes");

        var accounts = app.MapGroup("/api/v1/accounts").RequireAuthorization(AccessPolicies.Administrator);
        accounts.MapGet("/", async (HttpContext context, AccountAdministration service, int? page, CancellationToken token) =>
            page is < 1 or > (int.MaxValue / 50) ? Results.BadRequest() :
                await Guard(async () => Results.Ok(await service.ListAsync(context.User, page ?? 1, token))))
            .Produces<AccountPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("List accounts");
        accounts.MapGet("/{id:guid}", async (Guid id, HttpContext context, AccountAdministration service, CancellationToken token) =>
            await Guard(async () => (await service.GetAsync(context.User, id, token)) is { } account ? Results.Ok(account) : Results.NotFound()))
            .Produces<AccountSummary>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithName("GetAccount").WithSummary("Get an account");
        accounts.MapPost("/", async (CreateAccountRequest request, HttpContext context, AccountAdministration service, CancellationToken token) =>
            await Write(context, async () =>
            {
                if (!Valid(request.UserName, 256) || !Valid(request.Role, 64)) return Results.BadRequest();
                var result = await service.CreateAsync(context.User, request.UserName!, request.Role!, token);
                if (result.Result.Status != AccountOperationStatus.Success) return Error(result.Result);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Created($"/api/v1/accounts/{result.AccountId:D}", new AccountCreatedResponse(result.AccountId!.Value, result.Code!));
            })).RequireRateLimiting("identity-login").Produces<AccountCreatedResponse>(201).ProducesValidationProblem()
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(429)
            .WithSummary("Create an account and issue a one-time password setup code");
        accounts.MapPut("/{id:guid}/name", async (Guid id, RenameAccountRequest request, HttpContext context, AccountAdministration service, CancellationToken token) =>
            await Write(context, async () => !Valid(request.UserName, 256) ? Results.BadRequest() :
                Error(await service.RenameAsync(context.User, id, request.UserName!, token))))
            .RequireRateLimiting("identity-login").Produces(204).ProducesValidationProblem()
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429)
            .WithSummary("Rename an account");
        accounts.MapPut("/{id:guid}/role", async (Guid id, ChangeRoleRequest request, HttpContext context, AccountAdministration service, CancellationToken token) =>
            await Write(context, async () => !Valid(request.Role, 64) ? Results.BadRequest() :
                Error(await service.SetRoleAsync(context.User, id, request.Role!, token))))
            .RequireRateLimiting("identity-login").Produces(204).ProducesValidationProblem()
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429)
            .WithSummary("Change an account role");
        accounts.MapDelete("/{id:guid}", async (Guid id, HttpContext context, AccountAdministration service, CancellationToken token) =>
            await Write(context, async () => Error(await service.DeleteAsync(context.User, id, token))))
            .RequireRateLimiting("identity-login").Produces(204).ProducesProblem(400)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429)
            .WithSummary("Delete an account");
        accounts.MapPost("/{id:guid}/password-reset", async (Guid id, HttpContext context, AccountAdministration service, CancellationToken token) =>
            await Write(context, async () =>
            {
                var result = await service.IssuePasswordResetAsync(context.User, id, token);
                if (result.Result.Status != AccountOperationStatus.Success) return Error(result.Result);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new PasswordResetCodeResponse(result.Code!));
            })).RequireRateLimiting("identity-login").Produces<PasswordResetCodeResponse>().ProducesProblem(400)
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429)
            .WithSummary("Issue a one-time password reset code");

        app.MapPut("/api/v1/session/password", async (ChangePasswordRequest request, HttpContext context, SelfPasswordChange service, CancellationToken token) =>
            await Write(context, async () => !Valid(request.CurrentPassword, 1024) || !Valid(request.NewPassword, 1024)
                ? Results.BadRequest() : Error(await service.ChangeAsync(context.User, request.CurrentPassword!, request.NewPassword!, token))))
            .RequireAuthorization().RequireRateLimiting("identity-login").Produces(204).ProducesValidationProblem()
            .ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).WithSummary("Change the current user's password");
    }

    private static bool Valid(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static async Task<IResult> Write(HttpContext context, Func<Task<IResult>> operation)
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        return await Guard(operation);
    }
    private static async Task<IResult> Guard(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
    }
    private static IResult Error(AccountOperationResult result) => result.Status switch
    {
        AccountOperationStatus.Success => Results.NoContent(),
        AccountOperationStatus.NotFound => Results.NotFound(),
        AccountOperationStatus.InvalidRole => Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["Unsupported role."] }),
        AccountOperationStatus.ValidationFailed => Results.ValidationProblem(new Dictionary<string, string[]> { ["account"] = result.Errors.ToArray() }),
        _ => Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Account operation conflicts with the current state.",
            extensions: new Dictionary<string, object?> { ["code"] = result.Status.ToString() })
    };
}

/// <summary>Antiforgery token for a cookie-authenticated request.</summary>
public sealed record AntiforgeryResponse(string RequestToken);
/// <summary>Account name and built-in role.</summary>
public sealed record CreateAccountRequest(string? UserName, string? Role);
/// <summary>New account name.</summary>
public sealed record RenameAccountRequest(string? UserName);
/// <summary>New built-in role.</summary>
public sealed record ChangeRoleRequest(string? Role);
/// <summary>Current and new password.</summary>
public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
/// <summary>Created account and its single-use setup code.</summary>
public sealed record AccountCreatedResponse(Guid Id, string SetupCode);
/// <summary>Single-use password reset code.</summary>
public sealed record PasswordResetCodeResponse(string Code);

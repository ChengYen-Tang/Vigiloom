using Vigiloom.Contracts.Intelligence;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Security;

internal static class PublicSigmaEndpoints
{
    internal static void MapPublicSigma(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/admin/sigma")
            .RequireAuthorization("Administrator").RequireRateLimiting("api-client");
        admin.MapGet("/rules", async (string? cursor, IServiceProvider services, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!SigmaRuleInventory.ValidCursor(cursor)) return Results.BadRequest();
            var inventory = services.GetService<SigmaRuleInventory>();
            return inventory is null ? Results.StatusCode(503) : Results.Ok(await inventory.GetAsync(cursor, token));
        }).Produces<SigmaDetectionRulePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).Produces(StatusCodes.Status503ServiceUnavailable);
        admin.MapGet("/states", async (int? page, IServiceProvider services, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var selected = page ?? 1;
            if (selected is < 1 or > 1000) return Results.BadRequest();
            var inventory = services.GetService<SigmaRuleInventory>();
            return inventory is null ? Results.StatusCode(503) : Results.Ok(await inventory.GetStatesAsync(selected, token));
        }).Produces<SigmaRuleStatePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).Produces(StatusCodes.Status503ServiceUnavailable);
        admin.MapPost("/tests", async (SigmaRuleTestRequest request, IServiceProvider services, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!SigmaRuleService.Valid(request)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["samples"] = ["Invalid rule path or replay samples."] });
            var rules = services.GetService<SigmaRuleService>();
            if (rules is null) return Results.StatusCode(503);
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await rules.TestAsync(request, actor, token);
                return result.Status switch
                {
                    SigmaRuleTestStatus.Saved => Results.Ok(result.Result),
                    SigmaRuleTestStatus.RuleUnsupported => Results.Problem(statusCode: 422, title: "This rule uses unsupported syntax or log sources."),
                    SigmaRuleTestStatus.EventUnavailable => Results.Problem(statusCode: 409, title: "One or more source events are unavailable."),
                    _ => Results.Problem(statusCode: 409, title: "The rule source is unavailable or changed.")
                };
            }, allowApiCredential: false);
        }).Produces<SigmaRuleTestResponse>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(422).Produces(StatusCodes.Status503ServiceUnavailable);
        admin.MapPut("/state", async (SigmaRuleStateRequest request, IServiceProvider services, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!SigmaRuleService.Valid(request)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["rule"] = ["Invalid rule state request."] });
            var rules = services.GetService<SigmaRuleService>();
            if (rules is null) return Results.StatusCode(503);
            return await ApiMutation.ExecuteAsync(context, async actor => (await rules.SetAsync(request, actor, token)) switch
            {
                SigmaRuleChangeStatus.Changed or SigmaRuleChangeStatus.Unchanged => Results.NoContent(),
                SigmaRuleChangeStatus.TestUnavailable => Results.Problem(statusCode: 409, title: "A passing test for this rule version is required."),
                SigmaRuleChangeStatus.SourceUnavailable => Results.Problem(statusCode: 409, title: "The rule source is unavailable or changed."),
                _ => Results.Problem(statusCode: 409, title: "The rule setting changed; reload and try again.")
            }, allowApiCredential: false);
        }).Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).Produces(StatusCodes.Status503ServiceUnavailable);
    }
}

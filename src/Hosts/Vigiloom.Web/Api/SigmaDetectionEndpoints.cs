using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.HostMonitoring;

namespace Vigiloom.Web.Api;

internal static class SigmaDetectionEndpoints
{
    public static void MapSigmaDetectionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/sigma-detections", async (Guid deviceId, DateTimeOffset from,
            DateTimeOffset until, string? cursor, DetectionAccess history, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var page = await history.GetEventsAsync(deviceId, from, until, cursor, token);
                return page is null ? Results.NotFound() : Results.Ok(page);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid detection query."); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<SigmaDetectionEventPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Read private Sigma detection results for a device");

        app.MapGet("/api/v1/devices/{deviceId:guid}/sigma-gaps", async (Guid deviceId, DateTimeOffset from,
            DateTimeOffset until, string? cursor, DetectionAccess history, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var page = await history.GetGapsAsync(deviceId, from, until, cursor, token);
                return page is null ? Results.NotFound() : Results.Ok(page);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid detection query."); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<SigmaDetectionGapPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Read private Sigma detection data gaps for a device");

        var admin = app.MapGroup("/api/v1/admin/sigma")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/rules", async (string? cursor, SigmaRuleInventory rules, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!SigmaRuleInventory.ValidCursor(cursor)) return Results.BadRequest();
            return Results.Ok(await rules.GetAsync(cursor, token));
        }).Produces<SigmaDetectionRulePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("List Sigma rules and their test and activation state");
        admin.MapGet("/states", async (int? page, SigmaRuleInventory rules, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var selected = page ?? 1;
            return selected is < 1 or > 1000 ? Results.BadRequest() : Results.Ok(await rules.GetStatesAsync(selected, token));
        }).Produces<SigmaRuleStatePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("List saved Sigma rule settings, including unavailable sources");
        admin.MapPost("/tests", async (SigmaRuleTestRequest request, SigmaRuleService rules, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!SigmaRuleService.Valid(request)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["samples"] = ["Invalid rule path or replay samples."] });
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
        }).Produces<SigmaRuleTestResponse>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(422)
            .WithSummary("Test a Sigma rule against retained Agent events");

        admin.MapPut("/state", async (SigmaRuleStateRequest request, SigmaRuleService rules, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!SigmaRuleService.Valid(request)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["rule"] = ["Invalid rule state request."] });
            return await ApiMutation.ExecuteAsync(context, async actor => (await rules.SetAsync(request, actor, token)) switch
            {
                SigmaRuleChangeStatus.Changed or SigmaRuleChangeStatus.Unchanged => Results.NoContent(),
                SigmaRuleChangeStatus.TestUnavailable => Results.Problem(statusCode: 409, title: "A passing test for this rule version is required."),
                SigmaRuleChangeStatus.SourceUnavailable => Results.Problem(statusCode: 409, title: "The rule source is unavailable or changed."),
                _ => Results.Problem(statusCode: 409, title: "The rule setting changed; reload and try again.")
            }, allowApiCredential: false);
        }).Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409)
            .WithSummary("Enable a tested Sigma rule or disable its current state");
    }
}

using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentRolloutEndpoints
{
    public static void MapAgentRolloutEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/groups/{groupId:guid}/agent-rollouts/preview", async (Guid groupId, Guid packageId,
            AgentRollouts rollouts, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await rollouts.PreviewAsync(groupId, packageId, token) is { } preview ? Results.Ok(preview) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentRolloutPreview>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Preview the eligible devices for a group Agent rollout");

        app.MapPost("/api/v1/agent-rollouts", async (AgentRolloutInput input, AgentRollouts rollouts, HttpContext context, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await rollouts.CreateAsync(input, actor, token);
                return result.Result switch
                {
                    AgentRolloutResult.Saved => Results.Created($"/api/v1/agent-rollouts/{result.Id:D}",
                        new AgentRolloutCreated(result.Id!.Value, result.Included, result.Excluded)),
                    AgentRolloutResult.NotFound => Results.NotFound(),
                    AgentRolloutResult.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["rollout"] = ["No eligible Agents or invalid rollout settings."] }),
                    _ => Results.Problem(statusCode: 409, title: "The selected package is unavailable.")
                };
            }, allowApiCredential: false))
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces<AgentRolloutCreated>(201).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Create a group Agent update rollout");

        app.MapGet("/api/v1/groups/{groupId:guid}/agent-rollouts", async (Guid groupId, AgentRollouts rollouts, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await rollouts.ListAsync(groupId, token));
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<IReadOnlyList<AgentRolloutSummary>>().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("List recent group Agent update rollouts");

        app.MapGet("/api/v1/agent-rollouts/{id:guid}", async (Guid id, AgentRollouts rollouts, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await rollouts.GetAsync(id, token) is { } rollout ? Results.Ok(rollout) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentRolloutView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read a group Agent update rollout");

        app.MapPost("/api/v1/agent-rollouts/{id:guid}/state", async (Guid id, AgentRolloutChange input,
            AgentRollouts rollouts, HttpContext context, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
                await rollouts.ChangeAsync(id, input.Revision, input.Action, actor, token) switch
                {
                    AgentRolloutResult.Saved => Results.NoContent(),
                    AgentRolloutResult.NotFound => Results.NotFound(),
                    AgentRolloutResult.Invalid => Results.ValidationProblem(new Dictionary<string, string[]> { ["state"] = ["Invalid action or revision."] }),
                    _ => Results.Problem(statusCode: 409, title: "The rollout changed or the action is unavailable.")
                }, allowApiCredential: false))
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Pause, resume, or cancel a group Agent update rollout");
    }
}

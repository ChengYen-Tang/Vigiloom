using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentVersionEndpoints
{
    public static void MapAgentVersionEndpoints(this WebApplication app)
    {
        app.MapAgentRolloutEndpoints();
        app.MapGet("/api/v1/agent/version-assignment", async (AgentVersionAssignments assignments, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var result = await assignments.GetForAgentAsync(AgentEndpoints.Bearer(context), token);
            if (!result.Authenticated) return Results.Unauthorized();
            return result.Target is null ? Results.NoContent() : Results.Ok(result.Target);
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<AgentVersionTarget>().Produces(204).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Read the authenticated Agent's current version assignment");

        app.MapGet("/api/v1/agents/{agentId:guid}/version-assignment", async (Guid agentId, AgentVersionAssignments assignments,
            HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await assignments.GetAsync(agentId, token) is { } assignment ? Results.Ok(assignment) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentVersionAssignmentView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read a desired Agent package assignment");

        app.MapPut("/api/v1/agents/{agentId:guid}/version-assignment", async (Guid agentId, AgentVersionAssignmentUpdate input,
            AgentVersionAssignments assignments, HttpContext context, CancellationToken token) =>
        {
            if (input.Revision is null || input.Revision.Length > 32 || input.PackageId == Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["assignment"] = ["Invalid package or revision."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
                await assignments.SetAsync(agentId, input.PackageId, input.Revision, actor, token) switch
                {
                    VersionAssignmentResult.Saved => Results.NoContent(),
                    VersionAssignmentResult.NotFound => Results.NotFound(),
                    _ => Results.Problem(statusCode: 409, title: "Assignment changed or package is unavailable; reload and try again.")
                }, allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Assign or cancel a desired Agent package");
    }
}

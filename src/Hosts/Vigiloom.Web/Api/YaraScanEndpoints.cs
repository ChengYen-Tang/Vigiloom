using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class YaraScanEndpoints
{
    public static void MapYaraScanEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent/yara-scans/next", async (YaraScans scans, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var result = await scans.NextAsync(AgentEndpoints.Bearer(context), token);
            return !result.Authenticated ? Results.Unauthorized() : result.Job is null ? Results.NoContent() : Results.Ok(result.Job);
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<YaraScanJob>().Produces(204).ProducesProblem(401).ProducesProblem(403);

    }
}

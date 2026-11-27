using Microsoft.Net.Http.Headers;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentConfigurationEndpoints
{
    public static void MapAgentConfigurationEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent/settings", async (AgentConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            var snapshot = await settings.GetForAgentAsync(AgentEndpoints.Bearer(context), token);
            if (snapshot is null) return Results.Unauthorized();
            var tag = new EntityTagHeaderValue($"\"{snapshot.Revision}\"");
            context.Response.GetTypedHeaders().ETag = tag;
            context.Response.Headers.CacheControl = "private, no-store";
            var requested = context.Request.GetTypedHeaders().IfNoneMatch;
            if (requested?.Any(x => x.Equals(EntityTagHeaderValue.Any) || x.Compare(tag, useStrongComparison: false)) == true)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Ok(snapshot);
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<AgentSettingsSnapshot>().Produces(StatusCodes.Status304NotModified).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Read effective settings for the authenticated Agent");
    }

}

using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;
using Microsoft.Net.Http.Headers;

namespace Vigiloom.Web.Api;

internal static class IntegrityScopeEndpoints
{
    public static void MapIntegrityScopeEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent/integrity-settings", async (AgentIntegritySettings settings, HttpContext context, CancellationToken token) =>
        {
            var snapshot = await settings.GetAsync(AgentEndpoints.Bearer(context), token);
            if (snapshot is null) return Results.Unauthorized();
            var tag = new EntityTagHeaderValue($"\"{snapshot.Revision}\"");
            context.Response.GetTypedHeaders().ETag = tag;
            context.Response.Headers.CacheControl = "private, no-store";
            if (context.Request.GetTypedHeaders().IfNoneMatch?.Any(x => x.Equals(EntityTagHeaderValue.Any) || x.Compare(tag, useStrongComparison: false)) == true)
                return Results.StatusCode(StatusCodes.Status304NotModified);
            return Results.Ok(snapshot);
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<IntegritySettingsSnapshot>().Produces(304).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);

        app.MapGet("/api/v1/agent/integrity-status/{scopeId:guid}", async (Guid scopeId, AgentIntegrityStatus status, HttpContext context, CancellationToken token) =>
        {
            if (scopeId.Version != 7) return Results.BadRequest();
            context.Response.Headers.CacheControl = "private, no-store";
            try { return await status.GetAsync(AgentEndpoints.Bearer(context), scopeId, token) is { } value ? Results.Ok(value) : Results.Unauthorized(); }
            catch (TimeoutException) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<IntegrityScopeStatus>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429).ProducesProblem(503);
    }

}

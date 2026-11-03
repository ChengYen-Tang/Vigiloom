using Microsoft.Net.Http.Headers;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentQueueCapacityEndpoints
{
    public static void MapAgentQueueCapacityEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management/queue-capacity")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/platform", (AgentQueueCapacityConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, null, context, token))
            .Produces<AgentQueueCapacityView>().ProducesProblem(401).ProducesProblem(403).WithSummary("Read platform Agent queue capacity");
        admin.MapPut("/platform", (AgentQueueCapacityUpdate input, AgentQueueCapacityConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, null, input, context, token))
            .Produces<AgentQueueCapacityView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).WithSummary("Update platform Agent queue capacity");
        admin.MapGet("/devices/{deviceId:guid}", (Guid deviceId, AgentQueueCapacityConfiguration settings, HttpContext context, CancellationToken token) => ReadAsync(settings, deviceId, context, token))
            .Produces<AgentQueueCapacityView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).WithSummary("Read device Agent queue capacity overrides");
        admin.MapPut("/devices/{deviceId:guid}", (Guid deviceId, AgentQueueCapacityUpdate input, AgentQueueCapacityConfiguration settings, HttpContext context, CancellationToken token) => UpdateAsync(settings, deviceId, input, context, token))
            .Produces<AgentQueueCapacityView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).WithSummary("Update device Agent queue capacity overrides");

        app.MapGet("/api/v1/devices/{deviceId:guid}/agent-queue-capacity", async (Guid deviceId, AgentQueueCapacityConfiguration settings, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await settings.GetAsync(deviceId, token) is { } view ? Results.Ok(view.Effective) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentQueueCapacitySnapshot>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read effective Agent queue capacity for a device");

        app.MapGet("/api/v1/devices/{deviceId:guid}/agent-queue-capacity-status", async (Guid deviceId, AgentQueueCapacityMonitoring monitoring, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await monitoring.GetAsync(deviceId, token) is { } status ? Results.Ok(status) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentQueueCapacityStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read the Agent's observed queue capacity status");

        app.MapGet("/api/v1/agent/queue-capacity", async (AgentQueueCapacityConfiguration settings, HttpContext context, CancellationToken token) =>
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
            .Produces<AgentQueueCapacitySnapshot>().Produces(StatusCodes.Status304NotModified).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Read effective queue capacity for the authenticated Agent");
    }

    private static async Task<IResult> ReadAsync(AgentQueueCapacityConfiguration settings, Guid? deviceId, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        return await settings.GetAsync(deviceId, token) is { } view ? Results.Ok(view) : Results.NotFound();
    }

    private static async Task<IResult> UpdateAsync(AgentQueueCapacityConfiguration settings, Guid? deviceId, AgentQueueCapacityUpdate input, HttpContext context, CancellationToken token)
    {
        if (!AgentQueueCapacityConfiguration.Valid(input?.Overrides)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["capacity"] = ["Agent 佇列容量超出允許範圍。"] });
        if (deviceId is not null && await settings.GetAsync(deviceId, token) is null) return Results.NotFound();
        return await ApiMutation.ExecuteAsync(context, async actor =>
            await settings.UpdateAsync(deviceId, input!, actor, token) is { } saved ? Results.Ok(saved) : Results.Problem(statusCode: 409, title: "Agent queue capacity changed; reload and try again."), allowApiCredential: false);
    }
}

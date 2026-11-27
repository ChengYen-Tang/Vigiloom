using Vigiloom.Contracts.Agent;

namespace Vigiloom.Web.Api;

internal static class AgentQueueCapacityEndpoints
{
    public static void MapAgentQueueCapacityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/agent-queue-capacity-status", async (Guid deviceId, IAgentQueueCapacityStatusReader monitoring, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await monitoring.GetAsync(deviceId, token) is { } status ? Results.Ok(status) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentQueueCapacityStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read the Agent's observed queue capacity status");
    }
}

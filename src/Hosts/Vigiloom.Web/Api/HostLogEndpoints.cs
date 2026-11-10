using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.Api;

internal static class HostLogEndpoints
{
    public static void MapHostLogEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/host-logs", async (Guid deviceId, DateTimeOffset from,
            DateTimeOffset until, string? cursor, Guid? sourceId, HttpContext context, HostLogHistory history, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var page = await history.GetAsync(deviceId, from, until, cursor, sourceId, token);
                return page is null ? Results.Problem(statusCode: 404, title: "Device not found") : Results.Ok(page);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid host log query"); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<HostLogPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Read a bounded page of private host logs by receipt time");
    }
}

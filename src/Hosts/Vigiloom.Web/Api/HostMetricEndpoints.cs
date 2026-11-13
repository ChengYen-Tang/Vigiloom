using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.Api;

public static class HostMetricEndpoints
{
    public static void MapHostMetricEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/host-metrics/aggregate", async (Guid deviceId, Guid agentId,
            DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, string source, string? dimension,
            int bucketSeconds, HttpContext context, HostMetricHistory history, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var result = await history.AggregateAsync(deviceId, agentId, from, until, kind, source, dimension, bucketSeconds, token);
                return result is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Device or Agent not found") : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid metric aggregate query"); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<HostMetricAggregate>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Aggregate one Agent metric series into populated UTC buckets");
        app.MapGet("/api/v1/devices/{deviceId:guid}/host-metrics", async (Guid deviceId, DateTimeOffset from,
            DateTimeOffset until, string? cursor, AgentMetricKind? kind, HttpContext context, HostMetricHistory history, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var result = await history.GetAsync(deviceId, from, until, cursor, kind, token);
                return result is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Device not found") : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid metric query"); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<HostMetricPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Read a bounded page of a device's host metrics");
    }
}

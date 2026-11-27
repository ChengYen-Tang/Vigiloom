using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Infrastructure.Authentication;

namespace Vigiloom.HostMonitoring;

internal static class HostThresholdEndpoints
{
    public static void MapHostThresholdEndpoints(this WebApplication app)
    {
        const string route = "/api/v1/devices/{deviceId:guid}/host-metric-threshold";
        app.MapGet(route, async (Guid deviceId, Guid agentId, AgentMetricKind kind, string source, string? dimension,
            HttpContext context, HostMonitoringThresholds thresholds, IHttpClientFactory clients, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                if (!await ResourceDevices.ExistsAsync(clients, deviceId, token)) return Results.NotFound();
                var result = await thresholds.GetAsync(deviceId, agentId, kind, source, dimension, token);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid metric series"); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<HostMetricThresholdStatus>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        app.MapPut(route, async (Guid deviceId, Guid agentId, AgentMetricKind kind, string source, string? dimension,
            HostMetricThresholdInput input, HttpContext context, HostMonitoringThresholds thresholds, IHttpClientFactory clients, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                if (!await ResourceDevices.ExistsAsync(clients, deviceId, token) ||
                    await thresholds.GetAsync(deviceId, agentId, kind, source, dimension, token) is null) return Results.NotFound();
                return await ApiMutation.ExecuteAsync(context, async actor =>
                    await thresholds.UpdateAsync(deviceId, agentId, kind, source, dimension, input, actor, token) is { } saved
                        ? Results.Ok(saved) : Results.Problem(statusCode: 409, title: "Threshold changed; reload and try again."), allowApiCredential: false);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid metric threshold or series"); }
        }).RequireAuthorization("Administrator").RequireRateLimiting("api-client")
            .Produces<HostMetricThresholdView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429);
        app.MapPost("/internal/v1/host-threshold/query", async (HostMetricThresholdQuery query, HostMonitoringThresholds thresholds, IHttpClientFactory clients, CancellationToken token) =>
        {
            if (!await ResourceDevices.ExistsAsync(clients, query.DeviceId, token)) return Results.NotFound();
            try
            {
                var result = await thresholds.GetAsync(query.DeviceId, query.AgentId, query.Kind, query.Source, query.Dimension, token);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        app.MapPost("/internal/v1/host-threshold/update", async (HostMetricThresholdCommand command, HostMonitoringThresholds thresholds, IHttpClientFactory clients, CancellationToken token) =>
        {
            var query = command.Series;
            if (query is null || command.Input is null) return Results.BadRequest();
            if (!await ResourceDevices.ExistsAsync(clients, query.DeviceId, token)) return Results.NotFound();
            try
            {
                if (await thresholds.GetAsync(query.DeviceId, query.AgentId, query.Kind, query.Source, query.Dimension, token) is null) return Results.NotFound();
                var result = await thresholds.UpdateAsync(query.DeviceId, query.AgentId, query.Kind, query.Source, query.Dimension, command.Input, command.Actor, token);
                return result is null ? Results.Conflict() : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
    }
}

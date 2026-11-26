using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal static class HostThresholdEndpoints
{
    public static void MapHostThresholdEndpoints(this WebApplication app)
    {
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

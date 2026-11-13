using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class HostMetricThresholdEndpoints
{
    public static void MapHostMetricThresholdEndpoints(this WebApplication app)
    {
        const string route = "/api/v1/devices/{deviceId:guid}/host-metric-threshold";
        app.MapGet(route, async (Guid deviceId, Guid agentId, AgentMetricKind kind, string source, string? dimension,
            HttpContext context, HostMetricThresholds thresholds, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var result = await thresholds.GetAsync(deviceId, agentId, kind, source, dimension, token);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid metric series"); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
          .Produces<HostMetricThresholdStatus>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
          .WithSummary("Evaluate the current threshold for one exact host metric series");

        app.MapPut(route, async (Guid deviceId, Guid agentId, AgentMetricKind kind, string source, string? dimension,
            HostMetricThresholdInput input, HttpContext context, HostMetricThresholds thresholds, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                if (await thresholds.GetAsync(deviceId, agentId, kind, source, dimension, token) is null) return Results.NotFound();
                return await ApiMutation.ExecuteAsync(context, async actor =>
                    await thresholds.UpdateAsync(deviceId, agentId, kind, source, dimension, input, actor, token) is { } saved
                        ? Results.Ok(saved) : Results.Problem(statusCode: 409, title: "Threshold changed; reload and try again."), allowApiCredential: false);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid metric threshold or series"); }
        }).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
          .Produces<HostMetricThresholdView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429)
          .WithSummary("Update the threshold for one exact host metric series");
    }
}

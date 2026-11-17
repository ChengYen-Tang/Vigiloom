using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Application.HostMetrics;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

public static class HostMetricEndpoints
{
    public static void MapHostMetricEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/settings/host-metrics/retention", async (HostMetricRetentionSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)))
            .RequireAuthorization(AccessPolicies.Administrator).Produces<HostMetricRetentionSnapshot>().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Get host metric retention settings");
        app.MapPost("/api/v1/settings/host-metrics/retention/preview", async (HostMetricRetentionPreviewRequest input, HttpContext context, HostMetricRetentionSettings settings, CancellationToken token) =>
        {
            if (!HostMetricRetentionLimits.IsValid(input.RawDays, input.RollupDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.PreviewAsync(input, actor, token) is { } preview
                ? Results.Ok(preview) : Results.Conflict(), allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<HostMetricRetentionPreview>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Preview host metric retention changes");
        app.MapPut("/api/v1/settings/host-metrics/retention", async (HostMetricRetentionInput input, HttpContext context, HostMetricRetentionSettings settings, CancellationToken token) =>
        {
            if (!HostMetricRetentionLimits.IsValid(input.RawDays, input.RollupDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return await settings.UpdateAsync(input, actor, token) is { } saved ? Results.Ok(saved) : Results.Conflict(); }
                catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = ["A current preview is required."] }); }
            }, allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<HostMetricRetentionSnapshot>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Update host metric retention settings");
        app.MapGet("/api/v1/devices/{deviceId:guid}/host-metrics/series", async (Guid deviceId,
            DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, int? page, HttpContext context,
            HostMetricHistory history, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var result = await history.GetSeriesAsync(deviceId, from, until, kind, page ?? 0, token);
                return result is null ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Device not found") : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid metric series query"); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<HostMetricSeriesPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("List metric sources with samples or retained history");
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

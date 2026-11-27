using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal static class HostMetricEndpoints
{
    public static void MapHostMetricEndpoints(this WebApplication app)
    {
        Map(app, "/api/v1/devices/{deviceId:guid}/host-metrics", true);
        Map(app, "/internal/v1/devices/{deviceId:guid}/host-metrics", false);
    }

    private static void Map(WebApplication app, string path, bool external)
    {
        var group = app.MapGroup(path);
        if (external) group.RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
        group.MapGet("", ReadAsync).Produces<HostMetricPage>().ProducesProblem(400).ProducesProblem(404);
        group.MapGet("/series", SeriesAsync).Produces<HostMetricSeriesPage>().ProducesProblem(400).ProducesProblem(404);
        group.MapGet("/aggregate", AggregateAsync).Produces<HostMetricAggregate>().ProducesProblem(400).ProducesProblem(404);
    }

    private static async Task<IResult> ReadAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor,
        AgentMetricKind? kind, HostMetricReader history, IHttpClientFactory clients, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        try
        {
            var result = await history.GetAsync(deviceId, from, until, cursor, kind, token);
            return !await ResourceDevices.ExistsAsync(clients, deviceId, token) || result is null ? NotFound(context, "Device not found") : Results.Ok(result);
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid metric query"); }
    }

    private static async Task<IResult> SeriesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind,
        int? page, HostMetricReader history, IHttpClientFactory clients, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        try
        {
            var result = await history.GetSeriesAsync(deviceId, from, until, kind, page ?? 0, token);
            return !await ResourceDevices.ExistsAsync(clients, deviceId, token) || result is null ? NotFound(context, "Device not found") : Results.Ok(result);
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid metric series query"); }
    }

    private static async Task<IResult> AggregateAsync(Guid deviceId, Guid agentId, DateTimeOffset from, DateTimeOffset until,
        AgentMetricKind kind, string source, string? dimension, int bucketSeconds, HostMetricReader history,
        IHttpClientFactory clients, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "private, no-store";
        try
        {
            var result = await history.AggregateAsync(deviceId, agentId, from, until, kind, source, dimension, bucketSeconds, token);
            return !await ResourceDevices.ExistsAsync(clients, deviceId, token) || result is null ? NotFound(context, "Device or Agent not found") : Results.Ok(result);
        }
        catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid metric aggregate query"); }
    }

    private static IResult NotFound(HttpContext context, string title) => context.Request.Path.StartsWithSegments("/api/v1")
        ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: title) : Results.NotFound();
}

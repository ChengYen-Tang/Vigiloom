using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Observability;

internal static class ServiceReadEndpoints
{
    internal static void MapServiceReads(this WebApplication app)
    {
        var internalApi = app.MapGroup("/internal/v1/services/{id:guid}");
        internalApi.MapGet("/", DetailAsync);
        internalApi.MapGet("/instances", InstancesAsync);
        MapEvidence(internalApi);
        var external = app.MapGroup("/api/v1/service-details/{id:guid}").RequireAuthorization("MonitoringApiRead")
            .RequireRateLimiting("api-client");
        external.MapGet("/", DetailAsync);
        external.MapGet("/instances", InstancesAsync);
        MapEvidence(external);
    }

    private static void MapEvidence(RouteGroupBuilder group)
    {
        group.MapGet("/logs", async (Guid id, DateTimeOffset from, DateTimeOffset to, ServiceEvidenceReader reader,
            HttpContext context, CancellationToken token, int page = 1, string? traceId = null) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await reader.LogsAsync(id, from.ToUniversalTime(), to.ToUniversalTime(), page, traceId, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapGet("/traces", async (Guid id, DateTimeOffset from, DateTimeOffset to, ServiceEvidenceReader reader,
            HttpContext context, CancellationToken token, int page = 1) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await reader.TracesAsync(id, from.ToUniversalTime(), to.ToUniversalTime(), page, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapGet("/http-metrics", async (Guid id, ServiceHttpMetricsReader reader, HttpContext context, CancellationToken token) =>
        {
            if (id.Version != 7) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await reader.ReadAsync(id, token));
        });
        group.MapGet("/health-observations", async (Guid id, HealthObservationReader reader, HttpContext context, CancellationToken token, int page = 1) =>
        {
            if (id.Version != 7 || page is < 1 or > 1000) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await reader.PageAsync(id, page, token));
        });
    }

    private static async Task<IResult> DetailAsync(Guid id, ServiceDetailsReader reader, HttpContext context, CancellationToken token)
    {
        if (id.Version != 7) return Results.BadRequest();
        context.Response.Headers.CacheControl = "no-store";
        return await reader.ReadAsync(id, token) is { } detail ? Results.Ok(detail) : Results.NotFound();
    }

    private static async Task<IResult> InstancesAsync(Guid id, ServiceDetailsReader reader, HttpContext context, CancellationToken token, int page = 1)
    {
        if (id.Version != 7 || page is < 1 or > 1000) return Results.BadRequest();
        context.Response.Headers.CacheControl = "no-store";
        return await reader.InstancesAsync(id, page, token) is { } instances ? Results.Ok(instances) : Results.NotFound();
    }
}

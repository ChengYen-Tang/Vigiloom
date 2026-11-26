using Vigiloom.Modules.ServiceObservability.Application;

namespace Vigiloom.Observability;

internal static class ServiceReadEndpoints
{
    internal static void MapServiceReads(this WebApplication app)
    {
        var internalApi = app.MapGroup("/internal/v1/services/{id:guid}");
        internalApi.MapGet("/", DetailAsync);
        internalApi.MapGet("/instances", InstancesAsync);
        var external = app.MapGroup("/api/v1/service-details/{id:guid}").RequireAuthorization("MonitoringApiRead")
            .RequireRateLimiting("api-client");
        external.MapGet("/", DetailAsync);
        external.MapGet("/instances", InstancesAsync);
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

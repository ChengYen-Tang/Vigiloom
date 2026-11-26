using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Observability;

internal static class HealthReadEndpoints
{
    internal static void MapHealthReads(this WebApplication app)
    {
        var internalApi = app.MapGroup("/internal/v1/health/{id:guid}");
        internalApi.MapGet("/status", StatusAsync);
        internalApi.MapGet("/history", HistoryAsync);
        var external = app.MapGroup("/api/v1/service-health/{id:guid}").RequireAuthorization("MonitoringApiRead")
            .RequireRateLimiting("api-client");
        external.MapGet("/status", StatusAsync);
        external.MapGet("/history", HistoryAsync);
    }

    private static async Task<IResult> StatusAsync(Guid id, HealthStatusReader reader, HttpContext context, CancellationToken token)
    {
        if (id.Version != 7) return Results.BadRequest();
        context.Response.Headers.CacheControl = "no-store";
        return await reader.ReadAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound();
    }

    private static async Task<IResult> HistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to,
        HealthHistoryReader reader, HttpContext context, CancellationToken token)
    {
        context.Response.Headers.CacheControl = "no-store";
        try
        {
            return await reader.ReadAsync(id, from.ToUniversalTime(), to.ToUniversalTime(), token) is { } result
                ? Results.Ok(result) : Results.NotFound();
        }
        catch (HealthHistoryRequestException) { return Results.BadRequest(); }
        catch (HealthHistoryTooLargeException) { return Results.Problem(statusCode: 422, title: "The requested history is too large; select a shorter time range."); }
        catch (HealthHistoryBusyException) { return Results.Problem(statusCode: 503, title: "Health history changed; retry the request."); }
    }
}

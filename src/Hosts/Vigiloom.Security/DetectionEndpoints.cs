using System.Net.Http.Json;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Security;

internal static class DetectionEndpoints
{
    internal static void MapPublicDetections(this WebApplication app)
    {
        var devices = app.MapGroup("/api/v1/devices/{deviceId:guid}")
            .RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
        devices.MapGet("/sigma-detections", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor,
            HttpContext context, SigmaDetectionHistory history, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try { return await history.GetEventsAsync(deviceId, from, until, cursor, token) is { } page ? Results.Ok(page) : Results.NotFound(); }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid detection query."); }
        }).Produces<SigmaDetectionEventPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        devices.MapGet("/sigma-gaps", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor,
            HttpContext context, SigmaDetectionHistory history, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try { return await history.GetGapsAsync(deviceId, from, until, cursor, token) is { } page ? Results.Ok(page) : Results.NotFound(); }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid detection query."); }
        }).Produces<SigmaDetectionGapPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        devices.MapGet("/ioc-detections", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, int? page,
            HttpContext context, IocDetectionHistory history, IHttpClientFactory clients, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var result = await history.GetAsync(deviceId, from, until, page ?? 1, token);
                if (result is null) return Results.NotFound();
                var retention = await clients.CreateClient("observability").GetFromJsonAsync<TelemetryRetentionSnapshot>("internal/v1/telemetry-retention", token)
                    ?? throw new InvalidOperationException("Observation service returned an empty retention policy.");
                return Results.Ok(result with { RetentionDays = retention.LogDays });
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid detection query."); }
        }).Produces<IocDetectionPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
    }
}

using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Web.Api;

internal static class IocDetectionEndpoints
{
    public static void MapIocDetectionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/ioc-detections", async (Guid deviceId, DateTimeOffset from,
            DateTimeOffset until, int? page, IocDetectionHistory history, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try
            {
                var result = await history.GetAsync(deviceId, from, until, page ?? 1, token);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid detection query."); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<IocDetectionPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Read private IOC detection evidence for a device");
    }
}

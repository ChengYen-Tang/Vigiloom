using System.Security.Claims;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using Vigiloom.Web.Events;

namespace Vigiloom.Web.Api;

internal static class HostLogEndpoints
{
    public static void MapHostLogEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/host-logs", async (Guid deviceId, DateTimeOffset from,
            DateTimeOffset until, string? cursor, Guid? sourceId, HttpContext context, IHostLogHistory history, Vigiloom.Web.ServiceDetails.TelemetryRetentionAccess retention, EventTimelineAudit audit, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty) return Results.Forbid();
            try
            {
                var page = await history.GetAsync(deviceId, from, until, cursor, sourceId, token);
                if (page is null) return Results.Problem(statusCode: 404, title: "Device not found");
                var result = page with { RetentionDays = (await retention.GetAsync(token)).LogDays };
                await audit.RecordEvidenceAsync(actor, new EventEvidenceAccessed("host-log-list", deviceId, deviceId), token);
                return Results.Ok(result);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid host log query"); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<HostLogPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Read a bounded page of private host logs by receipt time");
    }
}

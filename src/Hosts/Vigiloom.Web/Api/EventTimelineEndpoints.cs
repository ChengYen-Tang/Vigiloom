using Vigiloom.Contracts.Events;
using Vigiloom.Web.Events;
using System.Security.Claims;

namespace Vigiloom.Web.Api;

internal static class EventTimelineEndpoints
{
    public static void MapEventTimelineEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/events", async (DateTimeOffset from, DateTimeOffset until, Guid? groupId, string? source,
            int? page, EventTimelineReader reader, EventTimelineAudit audit, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty) return Results.Forbid();
            try
            {
                var query = new EventTimelineQuery(from, until, groupId, source, page ?? 1);
                var result = await reader.GetAsync(query, token);
                await audit.RecordAsync(actor, query, result.Items.Count, false, token);
                return Results.Ok(result);
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid event query."); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<EventTimelinePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Read the private event timeline");

        app.MapGet("/api/v1/events/export", async (DateTimeOffset from, DateTimeOffset until, Guid? groupId, string? source,
            int? page, EventTimelineReader reader, EventTimelineAudit audit, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty) return Results.Forbid();
            try
            {
                var query = new EventTimelineQuery(from, until, groupId, source, page ?? 1);
                var result = await reader.GetAsync(query, token);
                await audit.RecordAsync(actor, query, result.Items.Count, true, token);
                return Results.File(EventTimelineCsv.Encode(result.Items), "text/csv; charset=utf-8", "vigiloom-events.csv");
            }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid event query."); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces(200, contentType: "text/csv").ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Export one page of the private event timeline");
    }
}

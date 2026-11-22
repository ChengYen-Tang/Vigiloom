using Vigiloom.Contracts.Events;
using Vigiloom.Web.Events;

namespace Vigiloom.Web.Api;

internal static class EventTimelineEndpoints
{
    public static void MapEventTimelineEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/events", async (DateTimeOffset from, DateTimeOffset until, Guid? groupId, string? source,
            int? page, EventTimelineReader reader, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try { return Results.Ok(await reader.GetAsync(new(from, until, groupId, source, page ?? 1), token)); }
            catch (ArgumentException) { return Results.Problem(statusCode: 400, title: "Invalid event query."); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<EventTimelinePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Read the private event timeline");
    }
}

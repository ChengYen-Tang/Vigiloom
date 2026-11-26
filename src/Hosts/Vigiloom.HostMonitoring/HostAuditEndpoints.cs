using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.HostMonitoring;

internal static class HostAuditEndpoints
{
    public static void MapHostAuditEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/v1/audit/query", async (AuditFeedRequest request, HostAuditFeed feed, CancellationToken token) =>
        {
            if (request.Query is null || !AuditFeedLimits.Valid(request.Query) || request.Source is not
                (AuditFeedSource.AgentSettings or AuditFeedSource.AgentQueueCapacity or AuditFeedSource.AgentMetricSettings or AuditFeedSource.HostMetricThresholds or AuditFeedSource.HostLogSources))
                return Results.BadRequest();
            return Results.Ok(await feed.QueryAsync(request.Source, request.Query, token));
        });
    }
}

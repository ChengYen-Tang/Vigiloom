using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;

namespace Vigiloom.Operations;

internal static class AuditEndpoints
{
    internal static void MapAuditEndpoints(this WebApplication app)
    {
        app.MapGet("/internal/v1/audit/{category}", async (string category, DateTimeOffset from, DateTimeOffset to,
            int? page, HttpContext context, OperationAuditQueryService audit, CancellationToken token) =>
        {
            var query = new OperationAuditQuery(from, to, page ?? 1);
            if (!OperationAuditQueryService.Valid(query)) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            var result = category switch
            {
                "general" => audit.QueryAsync(query, token),
                "ping-settings" => audit.QueryPingAsync(query, token),
                "audit-retention" => audit.QueryAuditRetentionAsync(query, token),
                "host-retention" => audit.QueryHostMetricRetentionAsync(query, token),
                "telemetry-retention" => audit.QueryTelemetryRetentionAsync(query, token),
                "agent-replay" => audit.QueryAgentReplayAsync(query, token),
                "agent-inbox" => audit.QueryAgentInboxAsync(query, token),
                "agent-packages" => audit.QueryAgentPackagesAsync(query, token),
                "agent-registration" => audit.QueryAgentRegistrationAsync(query, token),
                "service-approvals" => audit.QueryServiceApprovalsAsync(query, token),
                "service-sources" => audit.QueryServiceSourcesAsync(query, token),
                "groups" => audit.QueryGroupsAsync(query, token),
                "publication" => audit.QueryPublicationsAsync(query, token),
                "instance-policies" => audit.QueryInstancePoliciesAsync(query, token),
                "service-instances" => audit.QueryServiceInstancesAsync(query, token),
                "service-collections" => audit.QueryServiceCollectionsAsync(query, token),
                "log-quota" => audit.QueryLogQuotaAsync(query, token),
                "trace-quota" => audit.QueryTraceQuotaAsync(query, token),
                "collectors" => audit.QueryCollectorsAsync(query, token),
                "health-targets" => audit.QueryHealthTargetsAsync(query, token),
                "health-network-policies" => audit.QueryHealthNetworkPoliciesAsync(query, token),
                "intelligence" => audit.QueryIntelligenceAsync(query, token),
                "line" => audit.QueryLineAsync(query, token),
                "event-timeline" => audit.QueryEventTimelineAsync(query, token),
                _ => null
            };
            return result is null ? Results.NotFound() : Results.Ok(await result);
        });
    }
}

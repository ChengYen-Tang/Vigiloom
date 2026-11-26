using System.Globalization;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;

namespace Vigiloom.Web.Operations;

internal sealed class AuditQueryReader(OperationAuditQueryService local, IConfiguration configuration, IServiceProvider services)
{
    private Task<OperationAuditPage> ReadAsync(string category, OperationAuditQuery query,
        Func<OperationAuditQuery, CancellationToken, Task<OperationAuditPage>> fallback, CancellationToken token)
    {
        if (!OperationAuditQueryService.Valid(query)) throw new ArgumentException("Invalid audit query.");
        return string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"])
            ? fallback(query, token)
            : services.GetRequiredService<RemoteAuditQuery>().ReadAsync(category, query, token);
    }

    public Task<OperationAuditPage> QueryAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("general", query, local.QueryAsync, token);
    public Task<OperationAuditPage> QueryPingAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("ping-settings", query, local.QueryPingAsync, token);
    public Task<OperationAuditPage> QueryAuditRetentionAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("audit-retention", query, local.QueryAuditRetentionAsync, token);
    public Task<OperationAuditPage> QueryHostMetricRetentionAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("host-retention", query, local.QueryHostMetricRetentionAsync, token);
    public Task<OperationAuditPage> QueryTelemetryRetentionAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("telemetry-retention", query, local.QueryTelemetryRetentionAsync, token);
    public Task<OperationAuditPage> QueryAgentReplayAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("agent-replay", query, local.QueryAgentReplayAsync, token);
    public Task<OperationAuditPage> QueryAgentInboxAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("agent-inbox", query, local.QueryAgentInboxAsync, token);
    public Task<OperationAuditPage> QueryAgentPackagesAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("agent-packages", query, local.QueryAgentPackagesAsync, token);
    public Task<OperationAuditPage> QueryAgentRegistrationAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("agent-registration", query, local.QueryAgentRegistrationAsync, token);
    public Task<OperationAuditPage> QueryServiceApprovalsAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("service-approvals", query, local.QueryServiceApprovalsAsync, token);
    public Task<OperationAuditPage> QueryServiceSourcesAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("service-sources", query, local.QueryServiceSourcesAsync, token);
    public Task<OperationAuditPage> QueryGroupsAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("groups", query, local.QueryGroupsAsync, token);
    public Task<OperationAuditPage> QueryPublicationsAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("publication", query, local.QueryPublicationsAsync, token);
    public Task<OperationAuditPage> QueryInstancePoliciesAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("instance-policies", query, local.QueryInstancePoliciesAsync, token);
    public Task<OperationAuditPage> QueryServiceInstancesAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("service-instances", query, local.QueryServiceInstancesAsync, token);
    public Task<OperationAuditPage> QueryServiceCollectionsAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("service-collections", query, local.QueryServiceCollectionsAsync, token);
    public Task<OperationAuditPage> QueryLogQuotaAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("log-quota", query, local.QueryLogQuotaAsync, token);
    public Task<OperationAuditPage> QueryTraceQuotaAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("trace-quota", query, local.QueryTraceQuotaAsync, token);
    public Task<OperationAuditPage> QueryCollectorsAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("collectors", query, local.QueryCollectorsAsync, token);
    public Task<OperationAuditPage> QueryHealthTargetsAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("health-targets", query, local.QueryHealthTargetsAsync, token);
    public Task<OperationAuditPage> QueryHealthNetworkPoliciesAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("health-network-policies", query, local.QueryHealthNetworkPoliciesAsync, token);
    public Task<OperationAuditPage> QueryIntelligenceAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("intelligence", query, local.QueryIntelligenceAsync, token);
    public Task<OperationAuditPage> QueryLineAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("line", query, local.QueryLineAsync, token);
    public Task<OperationAuditPage> QueryEventTimelineAsync(OperationAuditQuery query, CancellationToken token) => ReadAsync("event-timeline", query, local.QueryEventTimelineAsync, token);
}

internal sealed class RemoteAuditQuery(HttpClient client)
{
    public async Task<OperationAuditPage> ReadAsync(string category, OperationAuditQuery query, CancellationToken token)
    {
        var path = QueryHelpers.AddQueryString($"internal/v1/audit/{category}", new Dictionary<string, string?>
        {
            ["from"] = query.From.ToString("O", CultureInfo.InvariantCulture),
            ["to"] = query.To.ToString("O", CultureInfo.InvariantCulture),
            ["page"] = query.Page.ToString(CultureInfo.InvariantCulture)
        });
        return await client.GetFromJsonAsync<OperationAuditPage>(path, token)
            ?? throw new InvalidOperationException("Operations service returned an empty audit page.");
    }
}


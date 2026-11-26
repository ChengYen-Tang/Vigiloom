using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using System.Security.Claims;
using System.Net.Http.Json;

namespace Vigiloom.Web.Diagnostics;

/// <summary>Routes one source at a time to its owning module; existing audit API stays unchanged.</summary>
internal sealed class AuditFeedReader(IdentityAuditReader identity, DeviceAuditFeed devices, AgentAuditFeed agent, SchedulingAuditFeed scheduling, InventoryAuditFeed inventory, IConfiguration configuration, IServiceProvider services, IHttpClientFactory clients)
{
    public static AuditFeedSource? Parse(string? value) => value switch
    {
        "accounts" => AuditFeedSource.Accounts,
        "api-credentials" => AuditFeedSource.ApiCredentials,
        "devices" => AuditFeedSource.Devices,
        "agent-identity" => AuditFeedSource.AgentIdentity,
        "agent-settings" => AuditFeedSource.AgentSettings,
        "agent-queue-capacity" => AuditFeedSource.AgentQueueCapacity,
        "agent-metric-settings" => AuditFeedSource.AgentMetricSettings,
        "host-metric-thresholds" => AuditFeedSource.HostMetricThresholds,
        "inventory-settings" => AuditFeedSource.InventorySettings,
        "host-log-sources" => AuditFeedSource.HostLogSources,
        "scheduling" => AuditFeedSource.Scheduling,
        _ => null
    };

    public async Task<AuditFeedPage> QueryAsync(ClaimsPrincipal principal, AuditFeedSource source, OperationAuditQuery query, CancellationToken token)
    {
        if (!AuditFeedLimits.Valid(query)) throw new ArgumentException("Invalid audit range.", nameof(query));
        return source switch
        {
            AuditFeedSource.Accounts or AuditFeedSource.ApiCredentials => await identity.QueryAsync(principal, source, query, token),
            AuditFeedSource.Devices => string.IsNullOrWhiteSpace(configuration["Services:Resources:Url"])
                ? await devices.QueryAsync(query, token) : await services.GetRequiredService<RemoteDeviceAuditFeed>().QueryAsync(query, token),
            AuditFeedSource.AgentIdentity => string.IsNullOrWhiteSpace(configuration["Services:Resources:Url"])
                ? await agent.QueryAsync(source, query, token) : await services.GetRequiredService<AgentIdentityAuditFeed>().QueryAsync(query, token),
            AuditFeedSource.AgentSettings or AuditFeedSource.AgentQueueCapacity or AuditFeedSource.AgentMetricSettings or AuditFeedSource.HostMetricThresholds or AuditFeedSource.HostLogSources =>
                string.IsNullOrWhiteSpace(configuration["Services:HostMonitoring:Url"])
                    ? await agent.QueryAsync(source, query, token)
                    : await services.GetRequiredService<RemoteHostAuditFeed>().QueryAsync(source, query, token),
            AuditFeedSource.Scheduling => Map(await scheduling.QueryAsync(query.From, query.To, query.Page, token)),
            AuditFeedSource.InventorySettings => string.IsNullOrWhiteSpace(configuration["Services:Security:Url"])
                ? await inventory.QueryAsync(query, token) : await SecurityInventoryAsync(query, token),
            _ => throw new ArgumentException("Unsupported audit source.", nameof(source))
        };
    }

    private async Task<AuditFeedPage> SecurityInventoryAsync(OperationAuditQuery query, CancellationToken token)
    {
        using var response = await clients.CreateClient("security").PostAsJsonAsync("internal/v1/inventory-settings/audit", query, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuditFeedPage>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty audit page.");
    }

    private static AuditFeedPage Map(SchedulingAuditPage page) => new(AuditFeedSource.Scheduling,
        page.Items.Select(item => new AuditFeedEntry(item.OccurredAt, item.ActorId, null, item.Action switch
        {
            SchedulingAuditAction.SettingsChanged => AuditFeedAction.ScheduleSettingsChanged,
            SchedulingAuditAction.Delete => AuditFeedAction.JobDeleteRequested,
            SchedulingAuditAction.Requeue => AuditFeedAction.JobRequeueRequested,
            SchedulingAuditAction.Enqueue => AuditFeedAction.JobEnqueueRequested,
            SchedulingAuditAction.RecurringRemove => AuditFeedAction.RecurringJobRemoveRequested,
            SchedulingAuditAction.RecurringTrigger => AuditFeedAction.RecurringJobTriggerRequested,
            _ => AuditFeedAction.Unknown
        }, item.Outcome switch
        {
            SchedulingAuditOutcome.Started => AuditFeedOutcome.Started,
            SchedulingAuditOutcome.Accepted => AuditFeedOutcome.Accepted,
            SchedulingAuditOutcome.Pending => AuditFeedOutcome.Pending,
            SchedulingAuditOutcome.Rejected => AuditFeedOutcome.Rejected,
            _ => AuditFeedOutcome.Unknown
        }, Reference: item.Reference)).ToArray(), page.Page, page.HasMore, page.LimitReached);
}

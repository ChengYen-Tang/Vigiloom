using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.IdentityAccess.Application;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

namespace Vigiloom.Web.Diagnostics;

/// <summary>Routes one source at a time to its owning module; existing audit API stays unchanged.</summary>
internal sealed class AuditFeedReader(IdentityAuditFeed identity, DeviceAuditFeed devices, AgentAuditFeed agent, SchedulingAuditFeed scheduling, InventoryAuditFeed inventory)
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
        "inventory-settings" => AuditFeedSource.InventorySettings,
        "host-log-sources" => AuditFeedSource.HostLogSources,
        "scheduling" => AuditFeedSource.Scheduling,
        _ => null
    };

    public async Task<AuditFeedPage> QueryAsync(AuditFeedSource source, OperationAuditQuery query, CancellationToken token)
    {
        if (!AuditFeedLimits.Valid(query)) throw new ArgumentException("Invalid audit range.", nameof(query));
        return source switch
        {
            AuditFeedSource.Accounts or AuditFeedSource.ApiCredentials => await identity.QueryAsync(source, query, token),
            AuditFeedSource.Devices => await devices.QueryAsync(query, token),
            AuditFeedSource.AgentIdentity or AuditFeedSource.AgentSettings or AuditFeedSource.AgentQueueCapacity or AuditFeedSource.AgentMetricSettings or AuditFeedSource.HostLogSources => await agent.QueryAsync(source, query, token),
            AuditFeedSource.Scheduling => Map(await scheduling.QueryAsync(query.From, query.To, query.Page, token)),
            AuditFeedSource.InventorySettings => await inventory.QueryAsync(query, token),
            _ => throw new ArgumentException("Unsupported audit source.", nameof(source))
        };
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

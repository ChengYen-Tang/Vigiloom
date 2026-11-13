using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Services;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Web.Groups;

namespace Vigiloom.Web.Workspace;

/// <summary>Combines independent source states without transferring an unbounded inventory to the browser.</summary>
internal sealed class WorkspaceOverviewReader(DeviceManagement devices, PingStore ping, AgentMonitoring agents,
    IDbContextFactory<ResourcesDbContext> resources, GroupManagement groups, ServiceMembership services,
    HealthStatusReader health, TimeProvider clock, ILogger<WorkspaceOverviewReader> logger)
{
    private const int DisplayLimit = 10;

    public async Task<WorkspaceOverview> ReadAsync(CancellationToken token)
    {
        var pingSection = Empty();
        var agentSection = Empty();
        var serviceSection = Empty();
        IReadOnlyList<WorkspaceGroup> groupItems = [];
        var groupError = false;
        try { (pingSection, agentSection) = await ReadDevicesAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Unable to load device overview"); pingSection = Empty(true); agentSection = Empty(true); }
        try { serviceSection = await ReadServicesAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Unable to load service overview"); serviceSection = Empty(true); }
        try
        {
            var page = await groups.ListAsync(null, 1, token);
            groupItems = page.Items.Take(DisplayLimit).Select(x => new WorkspaceGroup(x.Id, x.Name)).ToArray();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Unable to load workspace groups"); groupError = true; }
        return new(pingSection, agentSection, serviceSection, groupItems, groupError, clock.GetUtcNow());
    }

    private async Task<(WorkspaceSection Ping, WorkspaceSection Agents)> ReadDevicesAsync(CancellationToken token)
    {
        var pingItems = new List<WorkspaceItem>();
        var agentItems = new List<WorkspaceItem>();
        var pingCount = 0; var pingAttention = 0; var agentCount = 0; var agentAttention = 0;
        for (var page = 1; ; page++)
        {
            var batch = await devices.ListAsync(null, "name", false, false, page, token);
            if (batch.Items.Count == 0) break;
            var snapshots = batch.Items.Where(x => x.Enabled).Select(x => new PingDeviceSnapshot(x.Id,
                Convert.FromBase64String(x.Version), new[] { x.Address }.Concat(x.AdditionalTargets).ToArray())).ToArray();
            var pingStates = await ping.GetStatusesAsync(snapshots, token);
            var agentIds = batch.Items.Where(x => x.DeepMonitoring).Select(x => x.Id).ToArray();
            await using var db = await resources.CreateDbContextAsync(token);
            var agentDevices = await db.Devices.AsNoTracking().Where(x => agentIds.Contains(x.Id))
                .Select(x => new DeviceAgentState(x.Id, x.AgentAuthorizationGeneration, x.DeepMonitoring && x.IsEnabled && !x.IsArchived)).ToArrayAsync(token);
            var agentStates = await agents.GetConnectionsAsync(agentDevices, token);
            foreach (var device in batch.Items)
            {
                pingCount++;
                WorkspaceItem item;
                if (!device.Enabled) item = new(device.Id, device.Name, device.Address, "已停用", null, 3);
                else
                {
                    var status = pingStates[device.Id].Targets.OrderBy(PingPriority).First();
                    item = new(device.Id, device.Name, status.Target, status.DisplayState, status.MeasuredAt, PingPriority(status));
                }
                if (item.Priority < 2) pingAttention++;
                Keep(pingItems, item);
                if (!device.DeepMonitoring || !agentStates.TryGetValue(device.Id, out var agent)) continue;
                agentCount++;
                var priority = agent.State switch { AgentConnectionState.Offline => 0, AgentConnectionState.Online => 2, AgentConnectionState.Disabled => 3, _ => 1 };
                if (priority < 2) agentAttention++;
                Keep(agentItems, new(device.Id, device.Name, device.Address, AgentName(agent.State), agent.LastHeartbeatAt, priority));
            }
            if ((long)page * batch.PageSize >= batch.Total) break;
        }
        return (new(pingCount, pingAttention, pingItems), new(agentCount, agentAttention, agentItems));
    }

    private async Task<WorkspaceSection> ReadServicesAsync(CancellationToken token)
    {
        var items = new List<WorkspaceItem>();
        var count = 0; var attention = 0;
        for (var page = 1; ; page++)
        {
            var batch = await services.ListAsync(null, false, null, null, page, token);
            if (batch.Items.Count == 0) break;
            var states = await health.ReadManyAsync(batch.Items.Select(x => x.Id).ToArray(), token);
            foreach (var service in batch.Items)
            {
                if (!states.TryGetValue(service.Id, out var state)) continue;
                count++;
                var inactive = state.CollectionStopped || state.SourceRevoked || state.Status.UnknownReasons == ServiceHealthUnknownReason.Disabled;
                var priority = inactive ? 3 : state.Status.State switch { ServiceHealthState.Unhealthy => 0, ServiceHealthState.Degraded => 0, ServiceHealthState.Unknown => 1, _ => 2 };
                if (priority < 2) attention++;
                var name = state.CollectionStopped ? "收集已停止" : state.SourceRevoked ? "來源已撤銷" : state.Status.UnknownReasons == ServiceHealthUnknownReason.Disabled ? "健康檢查已停用" :
                    state.Status.State switch { ServiceHealthState.Healthy => "正常", ServiceHealthState.Degraded => "降級", ServiceHealthState.Unhealthy => "異常", _ => "未知或資料不足" };
                Keep(items, new(service.Id, service.Name, service.Environment, name, state.LastEvaluatedAt, priority));
            }
            if ((long)page * batch.PageSize >= batch.Total) break;
        }
        return new(count, attention, items);
    }

    private static int PingPriority(PingTargetStatus state) => state.State switch { Reachability.Unreachable => 0, Reachability.Unknown => 1, _ => 2 };
    private static string AgentName(AgentConnectionState state) => state switch
    {
        AgentConnectionState.Online => "連線中", AgentConnectionState.Offline => "離線", AgentConnectionState.Disabled => "已停用",
        AgentConnectionState.NotRegistered => "尚未註冊", AgentConnectionState.AwaitingFirstHeartbeat => "等待首次心跳",
        _ => "時鐘不確定"
    };
    private static WorkspaceSection Empty(bool unavailable = false) => new(0, 0, [], unavailable);
    private static void Keep(List<WorkspaceItem> items, WorkspaceItem item)
    {
        items.Add(item);
        items.Sort((a, b) => { var p = a.Priority.CompareTo(b.Priority); if (p != 0) return p; var n = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); return n != 0 ? n : a.Id.CompareTo(b.Id); });
        if (items.Count > DisplayLimit) items.RemoveAt(DisplayLimit);
    }
}

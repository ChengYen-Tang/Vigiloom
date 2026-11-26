using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Stores Agent heartbeat and queue-session state in the host database.</summary>
public sealed class HostHeartbeat(IDbContextFactory<HostMonitoringDbContext> contexts, TimeProvider clock)
{
    public async Task<AgentIdentityStatus> RecordAsync(RegisteredAgentIdentity identity, AgentHeartbeatReport? report, CancellationToken token)
    {
        if (report is not null && !report.IsValid()) throw new ArgumentException("Invalid Agent heartbeat report.", nameof(report));
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AgentDeviceLock.AcquireReceiptAsync(db, identity.AgentId, token);
        var agent = await GetOrCreateAsync(db, identity, token);
        if (agent is null) return AgentIdentityStatus.Invalid;
        var now = clock.GetUtcNow();
        agent.RecordHeartbeat(now);
        var staleSession = report is not null && agent.QueueReportEpoch != report.Epoch;
        if (report is not null && !staleSession && agent.RecordQueueStatus(report.Sequence, report.Queue, report.Quarantine, now))
        {
            agent.RecordMetricLossStatus(report.MetricLoss, report.Sequence, now);
            agent.RecordLogStatus(report.LogStatus, report.Sequence, now);
            agent.RecordInventoryConfiguration(report.InventoryConfiguration, report.Sequence, now);
            agent.RecordIntegrityConfiguration(report.IntegrityConfiguration, report.Sequence, now);
            agent.RecordBaselineConfiguration(report.BaselineConfiguration, now);
            agent.RecordRuntime(report.Runtime, now);
            if (report.Configuration is { } settings) agent.RecordConfigurationStatus(settings, now);
            if (report.CapacityConfiguration is { } capacity) agent.RecordQueueCapacityStatus(capacity, report.Sequence, now);
        }
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return staleSession ? AgentIdentityStatus.Conflict : AgentIdentityStatus.Success;
    }

    public async Task<AgentQueueSession?> GetSessionAsync(RegisteredAgentIdentity identity, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var agent = await db.AgentStatuses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == identity.AgentId, token);
        return agent is not null && (agent.DeviceId != identity.DeviceId || agent.DeviceGeneration != identity.DeviceGeneration)
            ? null : new AgentQueueSession(agent?.QueueReportEpoch);
    }

    public async Task<AgentIdentityStatus> BeginSessionAsync(RegisteredAgentIdentity identity, AgentQueueSessionRequest request, CancellationToken token)
    {
        if (request.Epoch.Version != 7 || request.ExpectedEpoch == request.Epoch) return AgentIdentityStatus.Invalid;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AgentDeviceLock.AcquireReceiptAsync(db, identity.AgentId, token);
        var agent = await GetOrCreateAsync(db, identity, token);
        if (agent is null) return AgentIdentityStatus.Invalid;
        if (agent.QueueReportEpoch == request.Epoch) return AgentIdentityStatus.Success;
        if (agent.QueueReportEpoch != request.ExpectedEpoch) return AgentIdentityStatus.Conflict;
        agent.BeginQueueSession(request.Epoch);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return AgentIdentityStatus.Success;
    }

    internal async Task<HostAgentStatus?> GetOrCreateAsync(HostMonitoringDbContext db, RegisteredAgentIdentity identity, CancellationToken token)
    {
        var agent = await db.AgentStatuses.SingleOrDefaultAsync(x => x.Id == identity.AgentId, token);
        if (agent is not null) return agent.DeviceId == identity.DeviceId && agent.DeviceGeneration == identity.DeviceGeneration ? agent : null;
        return db.AgentStatuses.Add(new HostAgentStatus(identity.AgentId, identity.DeviceId, identity.DeviceGeneration, clock.GetUtcNow())).Entity;
    }
}

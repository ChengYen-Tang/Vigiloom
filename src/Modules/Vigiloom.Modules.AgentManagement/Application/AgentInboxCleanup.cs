using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Deletes a bounded group of receipts under the same device lock used by the receiver.</summary>
public sealed class AgentInboxCleanup(IDbContextFactory<AgentManagementDbContext> contexts)
{
    public const int MaximumBatchSize = 200;
    private const int AgentPageSize = 100;
    private const int MaximumBatchesPerAgent = 10;

    /// <summary>Scans Agents in bounded pages; each Agent is committed independently.</summary>
    public async Task<int> RunAsync(CancellationToken token)
    {
        var total = 0;
        List<Exception>? failures = null;
        long after = 0;
        await using (var check = await contexts.CreateDbContextAsync(token))
        {
            var ready = await check.ReplayPolicies.AsNoTracking().AnyAsync(x => x.Phase == AgentReplayPhase.Enforced && x.CleanupCutoverAt != null, token);
            if (!ready) return 0;
        }
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var page = await db.Agents.AsNoTracking().Where(x => x.RowId > after && db.ReceivedBatches.Any(r => r.AgentId == x.Id))
                .OrderBy(x => x.RowId).Select(x => new { x.Id, x.RowId }).Take(AgentPageSize).ToArrayAsync(token);
            if (page.Length == 0)
            {
                if (failures is not null) throw new AggregateException($"Inbox cleanup failed for {failures.Count} Agent(s); {total} receipts were removed.", failures);
                return total;
            }
            foreach (var agent in page)
            {
                after = agent.RowId;
                try
                {
                    for (var batch = 0; batch < MaximumBatchesPerAgent; batch++)
                    {
                        var deleted = await CleanAgentAsync(agent.Id, MaximumBatchSize, token);
                        total = checked(total + deleted);
                        if (deleted < MaximumBatchSize) break;
                    }
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    (failures ??= []).Add(new InvalidOperationException($"Inbox cleanup failed for Agent {agent.Id}.", ex));
                }
            }
        }
    }

    public async Task<int> CleanAgentAsync(Guid agentId, int batchSize, CancellationToken token)
    {
        if (agentId == Guid.Empty || batchSize is < 1 or > MaximumBatchSize) throw new ArgumentOutOfRangeException(nameof(batchSize));
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var deviceId = await db.Agents.AsNoTracking().Where(x => x.Id == agentId).Select(x => (Guid?)x.DeviceId).SingleOrDefaultAsync(token);
        if (deviceId is null) return 0;
        await AgentDeviceLock.AcquireAsync(db, deviceId.Value, token);
        var agent = await db.Agents.SingleOrDefaultAsync(x => x.Id == agentId && x.DeviceId == deviceId, token);
        if (agent is null) return 0;
        var policy = await db.ReplayPolicies.FromSqlRaw("SELECT * FROM [agent].[ReplayPolicies] WITH (HOLDLOCK) WHERE [Scope] = 1")
            .AsNoTracking().SingleAsync(token);
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        if (policy.Phase != AgentReplayPhase.Enforced || policy.CleanupCutoverAt is not { } cutover ||
            policy.LegacyReceiptsRetainUntil is not { } legacyDeadline || policy.InboxSettingsChangedAt is not { } changed ||
            now < cutover || now < changed || agent.ReplayClockFloor is { } floor && now < floor)
            return 0;

        var previousWindow = await db.ReplayGenerations.AsNoTracking().Where(x => x.PolicyId == policy.Id && x.ValidThrough >= now)
            .MaxAsync(x => (int?)x.WindowSeconds, token);
        var boundary = now.AddSeconds(-Math.Max(policy.ReplayWindowSeconds, previousWindow ?? 0));
        var ids = await db.ReceivedBatches.AsNoTracking().Where(x => x.AgentId == agentId && x.EnqueuedAt < boundary &&
                (x.RetainUntil == null || x.RetainUntil <= now) &&
                (x.TrustedReceivedAt != null && x.TrustedReceivedAt >= cutover && x.RetainUntil != null || legacyDeadline <= now))
            .OrderBy(x => x.RowId).Select(x => x.RowId).Take(batchSize).ToArrayAsync(token);
        if (ids.Length == 0) return 0;
        // Capture the latest committed receipt before deleting any rows. This also covers
        // receipts written by an older receiver after the schema migration.
        var latest = await db.ReceivedBatches.AsNoTracking().Where(x => x.AgentId == agentId)
            .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.RowId)
            .Select(x => new { x.EnqueuedAt, x.ReceivedAt, x.RowId }).FirstAsync(token);
        agent.RecordCommittedBatch(latest.EnqueuedAt, latest.ReceivedAt, latest.RowId);
        var deleted = await db.ReceivedBatches.Where(x => x.AgentId == agentId && ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
        agent.AdvanceReplayClock(now);
        agent.AdvanceCleanupFence(boundary);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return deleted;
    }
}

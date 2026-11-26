using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

/// <summary>Removes expired Host receipts while preserving the replay fence.</summary>
public sealed class HostInboxCleanup(IDbContextFactory<HostMonitoringDbContext> contexts)
{
    private const int BatchSize = 200;

    public async Task<int> RunAsync(CancellationToken token)
    {
        var total = 0;
        long after = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var agents = await db.InboxStates.AsNoTracking().Where(x => x.RowId > after && db.ReceivedBatches.Any(r => r.AgentId == x.Id))
                .OrderBy(x => x.RowId).Select(x => new { x.Id, x.RowId }).Take(100).ToArrayAsync(token);
            if (agents.Length == 0) return total;
            foreach (var agent in agents)
            {
                after = agent.RowId;
                int removed;
                do
                {
                    removed = await CleanAgentAsync(agent.Id, token);
                    total = checked(total + removed);
                } while (removed == BatchSize);
            }
        }
    }

    internal async Task<int> CleanAgentAsync(Guid agentId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await AgentDeviceLock.AcquireReceiptAsync(db, agentId, token);
        var state = await db.InboxStates.SingleOrDefaultAsync(x => x.Id == agentId, token);
        if (state is null) return 0;
        var policy = await db.ReplayPolicies.FromSqlRaw("SELECT * FROM [agent].[ReplayPolicies] WITH (HOLDLOCK) WHERE [Scope] = 1")
            .AsNoTracking().SingleAsync(token);
        var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        if (policy.Phase != AgentReplayPhase.Enforced || policy.CleanupCutoverAt is not { } cutover ||
            policy.LegacyReceiptsRetainUntil is not { } legacyDeadline || policy.InboxSettingsChangedAt is not { } changed ||
            now < cutover || now < changed || state.ReplayClockFloor is { } floor && now < floor) return 0;
        var previousWindow = await db.ReplayGenerations.AsNoTracking().Where(x => x.PolicyId == policy.Id && x.ValidThrough >= now)
            .MaxAsync(x => (int?)x.WindowSeconds, token);
        var boundary = now.AddSeconds(-Math.Max(policy.ReplayWindowSeconds, previousWindow ?? 0));
        var expired = await db.ReceivedBatches.AsNoTracking().Where(x => x.AgentId == agentId && x.EnqueuedAt < boundary &&
                (x.RetainUntil == null || x.RetainUntil <= now) &&
                (x.TrustedReceivedAt != null && x.TrustedReceivedAt >= cutover && x.RetainUntil != null || legacyDeadline <= now))
            .OrderBy(x => x.RowId).Select(x => new { x.RowId, x.ByteCount }).Take(BatchSize).ToArrayAsync(token);
        if (expired.Length == 0) return 0;
        if (state.InboxReceiptCount is null || state.InboxReceiptPayloadBytes is null)
        {
            var usage = await db.ReceivedBatches.AsNoTracking().Where(x => x.AgentId == agentId).GroupBy(x => x.AgentId)
                .Select(group => new { Count = group.LongCount(), Bytes = group.Sum(x => (long)x.ByteCount) }).SingleAsync(token);
            state.InboxReceiptCount = usage.Count;
            state.InboxReceiptPayloadBytes = usage.Bytes;
        }
        var latest = await db.ReceivedBatches.AsNoTracking().Where(x => x.AgentId == agentId && !EF.Property<bool>(x, "PendingDelivery"))
            .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.RowId).FirstOrDefaultAsync(token);
        if (latest is not null) state.RecordCommittedBatch(latest);
        var ids = expired.Select(x => x.RowId).ToArray();
        var removed = await db.ReceivedBatches.Where(x => x.AgentId == agentId && ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
        if (removed != expired.Length || state.InboxReceiptCount < removed || state.InboxReceiptPayloadBytes < expired.Sum(x => (long)x.ByteCount))
            throw new InvalidOperationException("Agent receipt usage is inconsistent.");
        state.InboxReceiptCount = checked(state.InboxReceiptCount.Value - removed);
        state.InboxReceiptPayloadBytes = checked(state.InboxReceiptPayloadBytes.Value - expired.Sum(x => (long)x.ByteCount));
        state.InboxDeletedTotal = checked(state.InboxDeletedTotal + removed);
        state.InboxLastDeletedCount = removed;
        state.InboxLastDeletedAt = now;
        state.ReplayClockFloor = now;
        if (state.ReplayCleanedBefore is null || boundary > state.ReplayCleanedBefore) state.ReplayCleanedBefore = boundary;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return removed;
    }
}

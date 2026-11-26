using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Retention;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;

/// <summary>Removes expired application logs and spans and their unreferenced shared contexts.</summary>
public sealed class TelemetryRetentionCleanup(IDbContextFactory<ServiceObservabilityDbContext> contexts, IHostLogRetention? hostLogs = null, ISecurityDetectionRetention? securityDetections = null)
{
    private const int BatchSize = 500;

    public Task<(long Pruned, long Expired)> RunMetricsAsync(CancellationToken token) => new MetricRetentionCleanup(contexts).RunAsync(token);

    public async Task RunSecurityDetectionsAsync(CancellationToken token)
    {
        if (securityDetections is null) return;
        await TelemetryRetentionSettings.EnsureAsync(contexts, token);
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var policy = await db.TelemetryRetentionSettings.FromSqlRaw("SELECT * FROM [service].[TelemetryRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
            var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            var removed = await securityDetections.DeleteBatchAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), now, policy.LogDays, BatchSize, token);
            await tx.CommitAsync(token);
            if (removed < BatchSize) return;
        }
    }

    public async Task<long> RunHostLogsAsync(CancellationToken token)
    {
        if (hostLogs is null) throw new InvalidOperationException("Host log retention is not registered.");
        await TelemetryRetentionSettings.EnsureAsync(contexts, token);
        long total = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var policy = await db.TelemetryRetentionSettings.FromSqlRaw("SELECT * FROM [service].[TelemetryRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
            var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            var removed = securityDetections is null ? 0 : await securityDetections.DeleteBatchAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), now, policy.LogDays, BatchSize, token);
            await tx.CommitAsync(token);
            // Host-log cleanup reads Outbox; use a separate transaction from the security source lock.
            if (removed < BatchSize)
            {
                await using var hostDb = await contexts.CreateDbContextAsync(token);
                await using var hostTx = await hostDb.Database.BeginTransactionAsync(token);
                var hostPolicy = await hostDb.TelemetryRetentionSettings.FromSqlRaw("SELECT * FROM [service].[TelemetryRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
                var hostNow = await hostDb.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
                removed += await hostLogs.DeleteBatchAsync(hostDb.Database.GetDbConnection(), hostTx.GetDbTransaction(), hostNow, hostPolicy.LogDays, BatchSize - removed, token);
                await hostTx.CommitAsync(token);
            }
            total += removed;
            if (removed < BatchSize) return total;
        }
    }

    public async Task<(long Observations, long Intervals)> RunHealthAsync(CancellationToken token)
    {
        await TelemetryRetentionSettings.EnsureAsync(contexts, token);
        long observations = 0, intervals = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var policy = await db.TelemetryRetentionSettings.FromSqlRaw("SELECT * FROM [service].[TelemetryRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
            var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            var cutoff = now.AddDays(-policy.HealthDays);
            var observationIds = db.HealthObservations.Where(x => x.ObservedAt < cutoff &&
                    !db.HealthSchedules.Any(schedule => schedule.TargetId == x.TargetId && schedule.LastEvaluatedAt == x.ObservedAt))
                .OrderBy(x => x.ObservedAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
            var intervalIds = db.HealthStateIntervals.Where(x => x.EndedAt != null && x.EndedAt < cutoff)
                .OrderBy(x => x.EndedAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
            var removedObservations = await db.HealthObservations.Where(x => observationIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
            var removedIntervals = await db.HealthStateIntervals.Where(x => intervalIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
            if (removedIntervals > 0)
            {
                // The existing history reader uses this version to detect concurrent cleanup.
                db.Entry(policy).Property(x => x.HealthDays).IsModified = true;
                await db.SaveChangesAsync(token);
            }
            await tx.CommitAsync(token);
            observations += removedObservations; intervals += removedIntervals;
            if (removedObservations < BatchSize && removedIntervals < BatchSize) return (observations, intervals);
        }
    }

    public async Task<(long Logs, long Spans)> RunAsync(CancellationToken token)
    {
        await TelemetryRetentionSettings.EnsureAsync(contexts, token);
        long logs = 0, spans = 0;
        while (true)
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var policy = await db.TelemetryRetentionSettings.FromSqlRaw("SELECT * FROM [service].[TelemetryRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
            var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            var logCutoff = now.AddDays(-policy.LogDays);
            var traceCutoff = now.AddDays(-policy.TraceDays);
            var logIds = db.LogRecords.Where(x => x.OccurredAt < logCutoff).OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
            var traceIds = db.TraceSpans.Where(x => x.StartedAt < traceCutoff).OrderBy(x => x.StartedAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
            var removedLogs = await db.LogRecords.Where(x => logIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
            var removedSpans = await db.TraceSpans.Where(x => traceIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
            var logContextIds = db.LogContexts.Where(c => !db.LogRecords.Any(x => x.ContextId == c.RowId && x.ServiceId == c.ServiceId))
                .OrderBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
            var traceContextIds = db.TraceContexts.Where(c => !db.TraceSpans.Any(x => x.ContextId == c.RowId && x.ServiceId == c.ServiceId))
                .OrderBy(x => x.RowId).Select(x => x.RowId).Take(BatchSize);
            var removedLogContexts = await db.LogContexts.Where(x => logContextIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
            var removedTraceContexts = await db.TraceContexts.Where(x => traceContextIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
            await tx.CommitAsync(token);
            logs += removedLogs; spans += removedSpans;
            if (removedLogs < BatchSize && removedSpans < BatchSize && removedLogContexts < BatchSize && removedTraceContexts < BatchSize)
                return (logs, spans);
        }
    }

}

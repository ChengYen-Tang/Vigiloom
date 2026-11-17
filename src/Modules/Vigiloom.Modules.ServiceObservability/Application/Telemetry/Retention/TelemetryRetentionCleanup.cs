using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;

/// <summary>Removes expired application logs and spans and their unreferenced shared contexts.</summary>
public sealed class TelemetryRetentionCleanup(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    private const int BatchSize = 500;

    public Task<(long Pruned, long Expired)> RunMetricsAsync(CancellationToken token) => new MetricRetentionCleanup(contexts).RunAsync(token);

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

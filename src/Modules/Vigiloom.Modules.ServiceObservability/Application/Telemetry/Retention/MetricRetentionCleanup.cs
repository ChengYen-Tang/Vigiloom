using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;

/// <summary>Applies the application metric policy using the existing five-minute rollups.</summary>
internal sealed class MetricRetentionCleanup(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    private const int PageSize = 100;

    internal async Task<(long Pruned, long Expired)> RunAsync(CancellationToken token)
    {
        await TelemetryRetentionSettings.EnsureAsync(contexts, token);
        await using var query = await contexts.CreateDbContextAsync(token);
        var policy = await query.TelemetryRetentionSettings.AsNoTracking().SingleAsync(x => x.Slot == 1, token);
        var now = await query.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var cutoff = now.AddDays(-policy.MetricRawDays).AddMinutes(-5);
        var summaryCutoff = now.AddDays(-policy.MetricRollupDays).AddMinutes(-5);
        DateTimeOffset cursorStart = DateTimeOffset.UnixEpoch;
        long cursorRow = 0, pruned = 0, expired = 0;
        while (true)
        {
            var windows = await query.MetricRollupWindows.AsNoTracking()
                .Where(x => ((!x.RawPruned && x.WindowStart <= cutoff) || x.WindowStart <= summaryCutoff ||
                    query.Services.Any(service => service.Id == x.ServiceId && service.MetricRetainedAfter >= x.WindowStart.AddMinutes(5))) &&
                    (x.WindowStart > cursorStart || x.WindowStart == cursorStart && x.RowId > cursorRow))
                .OrderBy(x => x.WindowStart).ThenBy(x => x.RowId)
                .Select(x => new { x.RowId, x.ServiceId, x.WindowStart }).Take(PageSize).ToArrayAsync(token);
            if (windows.Length == 0) break;
            foreach (var window in windows)
            {
                int result;
                do { result = await ProcessAsync(window.ServiceId, window.RowId, token); } while (result == 3);
                if (result == 1) pruned++;
                if (result == 2) expired++;
            }
            cursorStart = windows[^1].WindowStart;
            cursorRow = windows[^1].RowId;
        }
        while (true)
        {
            var ids = query.MetricContexts.Where(x => !query.MetricPoints.Any(point => point.ContextId == x.RowId && point.ServiceId == x.ServiceId))
                .OrderBy(x => x.RowId).Select(x => x.RowId).Take(500);
            if (await query.MetricContexts.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token) < 500) break;
        }
        return (pruned, expired);
    }

    internal async Task<int> ProcessAsync(Guid serviceId, long rowId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var policy = await db.TelemetryRetentionSettings.FromSqlRaw("SELECT * FROM [service].[TelemetryRetentionSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1").SingleAsync(token);
        if (!await ServiceInstanceResolver.LockServiceAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(), serviceId, token)) return 0;
        var state = await db.MetricRollupWindows.SingleOrDefaultAsync(x => x.RowId == rowId && x.ServiceId == serviceId, token);
        if (state is null) return 0;
        var service = await db.Services.SingleAsync(x => x.Id == serviceId, token);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var end = state.WindowStart.AddMinutes(5);
        var expiring = service.MetricRetainedAfter >= end;
        if (!expiring && end > now.AddDays(-policy.MetricRawDays)) return 0;

        if (expiring || end <= now.AddDays(-policy.MetricRollupDays))
        {
            // Advance the rejection boundary in time order so existing older windows remain readable.
            if (await db.MetricRollupWindows.AnyAsync(x => x.ServiceId == serviceId && x.WindowStart < state.WindowStart, token)) return 0;
            var (safe, finished) = await MetricWindowPruning.RemoveExpiredBatchAsync(db, state, token);
            if (!safe) return 0;
            if (service.MetricRetainedAfter is null || service.MetricRetainedAfter < end) service.MetricRetainedAfter = end;
            if (!finished)
            {
                // Invalidate readers which captured this window before the first batch committed.
                db.Entry(state).Property(x => x.InputRevision).IsModified = true;
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return 3;
            }
            await db.MetricRollupSeries.Where(x => x.WindowRowId == state.RowId).ExecuteDeleteAsync(token);
            db.MetricRollupWindows.Remove(state);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return 2;
        }

        if (!await MetricWindowPruning.PruneAsync(db, state, token)) return 0;
        await transaction.CommitAsync(token);
        return 1;
    }
}

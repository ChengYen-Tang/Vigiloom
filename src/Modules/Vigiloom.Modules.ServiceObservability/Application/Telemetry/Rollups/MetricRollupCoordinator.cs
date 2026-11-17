using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Schedules closed, changed metric windows for the existing materializer.</summary>
public sealed class MetricRollupCoordinator(
    IDbContextFactory<ServiceObservabilityDbContext> contexts,
    MetricRollupMaterializer materializer,
    MetricWindowBackfill backfill,
    ILogger<MetricRollupCoordinator> logger)
{
    private const int PageSize = 100;

    public async Task RunAsync(CancellationToken token)
    {
        await backfill.RunAsync(token);
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var lastRow = await db.MetricRollupWindows.MaxAsync(x => (long?)x.RowId, token) ?? 0;
        var cursor = 0L;
        while (cursor < lastRow)
        {
            var windows = await db.MetricRollupWindows.AsNoTracking()
                .Where(x => x.RowId > cursor && x.RowId <= lastRow && !x.RawPruned &&
                    x.WindowStart <= now.AddMinutes(-5) &&
                    (x.ProjectionVersion == null || x.ProjectionVersion <= MetricRollupMaterializer.ProjectionVersion) &&
                    (x.ProjectedRevision == null || x.ProjectedRevision != x.InputRevision ||
                     x.ProjectionVersion < MetricRollupMaterializer.ProjectionVersion))
                .OrderBy(x => x.RowId).Select(x => new { x.RowId, x.ServiceId, x.WindowStart })
                .Take(PageSize).ToArrayAsync(token);
            if (windows.Length == 0) break;
            foreach (var window in windows)
            {
                try { await materializer.MaterializeAsync(window.ServiceId, window.WindowStart, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning("Metric rollup failed for service {ServiceId} and window {WindowStart}: {ErrorType}",
                        window.ServiceId, window.WindowStart, ex.GetType().Name);
                }
            }
            cursor = windows[^1].RowId;
        }
    }
}

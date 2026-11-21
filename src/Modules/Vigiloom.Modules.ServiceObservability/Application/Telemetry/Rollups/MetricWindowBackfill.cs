using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Discovers windows for metrics stored before window revision tracking was introduced.</summary>
public sealed class MetricWindowBackfill(IDbContextFactory<ServiceObservabilityDbContext> contexts, ILogger<MetricWindowBackfill> logger)
{
    private const int PageSize = 100;

    public async Task RunAsync(CancellationToken token)
    {
        await using var query = await contexts.CreateDbContextAsync(token);
        var lastRow = await query.MetricContexts.MaxAsync(x => (long?)x.RowId, token) ?? 0;
        var cursor = 0L;
        while (cursor < lastRow)
        {
            var pending = await query.MetricContexts.AsNoTracking()
                .Where(x => !x.WindowsTracked && x.RowId > cursor && x.RowId <= lastRow)
                .OrderBy(x => x.RowId).Select(x => new { x.RowId, x.ServiceId }).Take(PageSize).ToArrayAsync(token);
            if (pending.Length == 0) break;
            foreach (var item in pending)
            {
                try { await TrackAsync(item.RowId, item.ServiceId, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning("Metric window backfill failed for service {ServiceId}: {ErrorType}", item.ServiceId, ex.GetType().Name);
                }
            }
            cursor = pending[^1].RowId;
        }
    }

    private async Task TrackAsync(long id, Guid serviceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        if (!await ServiceInstanceResolver.LockServiceAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), serviceId, token)) return;
        var context = await db.MetricContexts.SingleOrDefaultAsync(x => x.RowId == id && x.ServiceId == serviceId, token);
        if (context is null || context.WindowsTracked) return;
        var metadata = Metric.Parser.ParseFrom(context.MetricPayload);
        var delta = metadata.DataCase switch
        {
            Metric.DataOneofCase.Sum => metadata.Sum.AggregationTemporality == AggregationTemporality.Delta,
            Metric.DataOneofCase.Histogram => metadata.Histogram.AggregationTemporality == AggregationTemporality.Delta,
            Metric.DataOneofCase.ExponentialHistogram => metadata.ExponentialHistogram.AggregationTemporality == AggregationTemporality.Delta,
            _ => false
        };
        var starts = new HashSet<DateTimeOffset>();
        var points = db.MetricPoints.AsNoTracking().Where(x => x.ContextId == id).Select(x => new { x.Kind, x.PointPayload });
        await foreach (var point in points.AsAsyncEnumerable().WithCancellation(token))
        {
            var time = point.Kind switch
            {
                1 or 2 => NumberDataPoint.Parser.ParseFrom(point.PointPayload).TimeUnixNano,
                3 => HistogramDataPoint.Parser.ParseFrom(point.PointPayload).TimeUnixNano,
                4 => ExponentialHistogramDataPoint.Parser.ParseFrom(point.PointPayload).TimeUnixNano,
                5 => SummaryDataPoint.Parser.ParseFrom(point.PointPayload).TimeUnixNano,
                _ => 0UL
            };
            if (!(delta ? MetricWindow.TryForDeltaEnd(time, out var window) : MetricWindow.TryForSample(time, out window)))
                throw new InvalidOperationException("Metric point has no valid time window.");
            starts.Add(DateTimeOffset.UnixEpoch.AddTicks(checked((long)(window.Start / 100))));
        }
        var changes = new Dictionary<Guid, HashSet<DateTimeOffset>> { [serviceId] = starts };
        if (await MetricWindowRevisions.MarkAsync(db, changes, token) != WindowRevisionResult.Updated)
            throw new InvalidOperationException("Metric windows could not be tracked.");
        context.WindowsTracked = true;
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
    }
}

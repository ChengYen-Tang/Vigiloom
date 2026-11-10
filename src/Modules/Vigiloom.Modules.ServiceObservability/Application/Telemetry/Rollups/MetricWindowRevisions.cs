using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

internal enum WindowRevisionResult { Updated, RawPruned, CapacityExceeded }

/// <summary>Marks windows changed by a raw metric transaction. The caller holds every affected service lock.</summary>
internal static class MetricWindowRevisions
{
    private const int QueryChunk = 1000;

    internal static bool Add(Dictionary<Guid, HashSet<DateTimeOffset>> changes, Guid serviceId, MetricIngestion.MetricGroup data)
    {
        var delta = data.Metadata.DataCase switch
        {
            Metric.DataOneofCase.Sum => data.Metadata.Sum.AggregationTemporality == AggregationTemporality.Delta,
            Metric.DataOneofCase.Histogram => data.Metadata.Histogram.AggregationTemporality == AggregationTemporality.Delta,
            Metric.DataOneofCase.ExponentialHistogram => data.Metadata.ExponentialHistogram.AggregationTemporality == AggregationTemporality.Delta,
            _ => false
        };
        if (!changes.TryGetValue(serviceId, out var windows)) changes.Add(serviceId, windows = []);
        foreach (var point in data.Points)
        {
            if (!(delta ? MetricWindow.TryForDeltaEnd(point.Time, out var window) : MetricWindow.TryForSample(point.Time, out window)))
                return false;
            windows.Add(DateTimeOffset.UnixEpoch.AddTicks(checked((long)(window.Start / 100))));
        }
        return true;
    }

    internal static async Task<WindowRevisionResult> MarkAsync(ServiceObservabilityDbContext db,
        IReadOnlyDictionary<Guid, HashSet<DateTimeOffset>> changes, CancellationToken token)
    {
        foreach (var (serviceId, windows) in changes.OrderBy(pair => pair.Key))
        {
            var existing = new Dictionary<DateTimeOffset, MetricRollupWindow>();
            foreach (var chunk in windows.Chunk(QueryChunk))
            {
                var records = await db.MetricRollupWindows.Where(x => x.ServiceId == serviceId && chunk.Contains(x.WindowStart)).ToListAsync(token);
                foreach (var record in records) existing.Add(record.WindowStart, record);
            }
            if (existing.Values.Any(x => x.RawPruned)) return WindowRevisionResult.RawPruned;
            if (existing.Values.Any(x => x.InputRevision == long.MaxValue)) return WindowRevisionResult.CapacityExceeded;
            foreach (var start in windows)
            {
                if (existing.TryGetValue(start, out var window)) window.InputRevision++;
                else db.MetricRollupWindows.Add(new MetricRollupWindow { ServiceId = serviceId, WindowStart = start, InputRevision = 1 });
            }
        }
        return WindowRevisionResult.Updated;
    }
}

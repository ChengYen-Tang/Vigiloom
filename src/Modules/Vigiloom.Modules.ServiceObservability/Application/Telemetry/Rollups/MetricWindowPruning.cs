using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Removes raw points only when their existing five-minute result can replace them.</summary>
internal static class MetricWindowPruning
{
    /// <remarks>The caller holds the service lock and a transaction, and has checked the retention cutoff.</remarks>
    internal static async Task<bool> PruneAsync(ServiceObservabilityDbContext db, MetricRollupWindow state, CancellationToken token)
    {
        if (state.RawPruned || !state.IsComplete || state.ProjectedRevision != state.InputRevision ||
            state.ProjectionVersion != MetricRollupMaterializer.ProjectionVersion)
            return false;

        if (!await RemoveRawAsync(db, state, token)) return false;
        state.RawPruned = true;
        await db.SaveChangesAsync(token);
        return true;
    }

    /// <summary>Removes one expired window's points while preserving points in neighboring windows.</summary>
    internal static async Task<bool> RemoveRawAsync(ServiceObservabilityDbContext db, MetricRollupWindow state, CancellationToken token)
    {
        if (await db.MetricContexts.AnyAsync(x => x.ServiceId == state.ServiceId && !x.WindowsTracked, token)) return false;

        var nanos = checked((ulong)(state.WindowStart - DateTimeOffset.UnixEpoch).Ticks * 100);
        if (!MetricWindow.TryForSample(nanos, out var window) || window.Start != nanos) return false;
        var candidates = await MetricWindowInputs.ReadAsync(db, state.ServiceId, window, token);
        if (candidates.CapacityExceeded) return false;

        var metadata = new Dictionary<long, Metric>();
        var ids = new List<long>();
        try
        {
            foreach (var point in candidates.Points)
            {
                if (!metadata.TryGetValue(point.ContextId, out var metric))
                    metadata.Add(point.ContextId, metric = Metric.Parser.ParseFrom(point.Context.MetricPayload));
                if (!MetricWindowGrouping.TryParse(point.Kind, metric, point.PointPayload, out _, out _, out var timestamp, out var delta) ||
                    !(delta ? MetricWindow.TryForDeltaEnd(timestamp, out var actual) : MetricWindow.TryForSample(timestamp, out actual)))
                    return false;
                if (actual == window) ids.Add(point.RowId);
            }
        }
        catch (InvalidProtocolBufferException) { return false; }

        if (ids.Count > 0)
            await db.MetricPoints.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
        return true;
    }

    /// <summary>Removes a bounded batch after both raw and summary retention have expired.</summary>
    internal static async Task<(bool Safe, bool Finished)> RemoveExpiredBatchAsync(ServiceObservabilityDbContext db, MetricRollupWindow state, CancellationToken token)
    {
        if (await db.MetricContexts.AnyAsync(x => x.ServiceId == state.ServiceId && !x.WindowsTracked, token)) return (false, false);
        var nanos = checked((ulong)(state.WindowStart - DateTimeOffset.UnixEpoch).Ticks * 100);
        if (!MetricWindow.TryForSample(nanos, out var window) || window.Start != nanos) return (false, false);
        var from = state.WindowStart;
        var through = from.AddMinutes(5);
        long cursor = 0;
        while (true)
        {
            var points = await db.MetricPoints.AsNoTracking()
                .Where(x => x.ServiceId == state.ServiceId && x.OccurredAt >= from && x.OccurredAt <= through && x.RowId > cursor)
                .OrderBy(x => x.RowId).Select(x => new { x.RowId, x.ContextId, x.Kind, x.PointPayload, x.Context.MetricPayload })
                .Take(100).ToArrayAsync(token);
            if (points.Length == 0) return (true, true);
            var ids = new List<long>(points.Length);
            var metadata = new Dictionary<long, Metric>();
            try
            {
                foreach (var point in points)
                {
                    if (!metadata.TryGetValue(point.ContextId, out var metric))
                        metadata.Add(point.ContextId, metric = Metric.Parser.ParseFrom(point.MetricPayload));
                    if (!MetricWindowGrouping.TryParse(point.Kind, metric, point.PointPayload, out _, out _, out var timestamp, out var delta) ||
                        !(delta ? MetricWindow.TryForDeltaEnd(timestamp, out var actual) : MetricWindow.TryForSample(timestamp, out actual)))
                        return (false, false);
                    if (actual == window) ids.Add(point.RowId);
                }
            }
            catch (InvalidProtocolBufferException) { return (false, false); }
            if (ids.Count > 0)
            {
                await db.MetricPoints.Where(x => ids.Contains(x.RowId)).ExecuteDeleteAsync(token);
                return (true, false);
            }
            cursor = points[^1].RowId;
        }
    }
}

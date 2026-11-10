using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

public enum MetricRollupStatus { Complete, Incomplete, Current, Busy, ServiceNotFound, NoData, WindowOpen, RawPruned, CapacityExceeded, VersionUnsupported }

/// <summary>Materializes one closed service window. Scheduling and retention belong to the retention coordinator.</summary>
public sealed class MetricRollupMaterializer(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    internal const int ProjectionVersion = 2;

    /// <remarks>Call only after all metric writers have been upgraded to track window revisions.
    /// Results marked incomplete, pruned or outdated must never authorize raw cleanup.</remarks>
    public async Task<MetricRollupStatus> MaterializeAsync(Guid serviceId, DateTimeOffset windowStart, CancellationToken token)
    {
        if (serviceId.Version != 7 || windowStart.Offset != TimeSpan.Zero || windowStart < DateTimeOffset.UnixEpoch)
            throw new ArgumentException("A service and aligned UTC window start are required.");
        var ticks = (ulong)(windowStart - DateTimeOffset.UnixEpoch).Ticks;
        if (ticks > ulong.MaxValue / 100 || !MetricWindow.TryForSample(ticks * 100, out var window) ||
            window.Start != ticks * 100)
            throw new ArgumentException("A service and aligned UTC window start are required.");

        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var sourceId = await db.Services.AsNoTracking().Where(x => x.Id == serviceId).Select(x => (Guid?)x.SourceId).SingleOrDefaultAsync(token);
        if (sourceId is null) return MetricRollupStatus.ServiceNotFound;
        if (!await ServiceInstanceResolver.LockServiceAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), serviceId, token))
            return MetricRollupStatus.Busy;
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        if (windowStart.AddMinutes(5) > now) return MetricRollupStatus.WindowOpen;

        var state = await db.MetricRollupWindows.SingleOrDefaultAsync(x => x.ServiceId == serviceId && x.WindowStart == windowStart, token);
        if (state?.ProjectionVersion > ProjectionVersion) return MetricRollupStatus.VersionUnsupported;
        if (state?.ProjectedRevision == state?.InputRevision && state?.ProjectionVersion == ProjectionVersion)
            return state!.IsComplete ? MetricRollupStatus.Current : MetricRollupStatus.Incomplete;
        if (state?.RawPruned == true) return MetricRollupStatus.RawPruned;
        var input = await MetricWindowInputs.ReadAsync(db, serviceId, window, token);
        if (input.CapacityExceeded) return MetricRollupStatus.CapacityExceeded;
        var grouped = MetricWindowGrouping.Group(sourceId.Value, serviceId, window, input.Points);
        if (grouped.Series.Count == 0 && !grouped.RequiresRaw) return MetricRollupStatus.NoData;

        if (state is null)
        {
            // An explicit window may contain data written before revision tracking was deployed.
            state = new MetricRollupWindow { ServiceId = serviceId, WindowStart = windowStart, InputRevision = 1 };
            db.MetricRollupWindows.Add(state);
            await db.SaveChangesAsync(token);
        }
        await db.MetricRollupSeries.Where(x => x.WindowRowId == state.RowId).ExecuteDeleteAsync(token);
        var complete = !grouped.RequiresRaw;
        var collisions = new Dictionary<ByteString, int>();
        foreach (var series in grouped.Series)
        {
            var result = MetricRollupEncoding.Encode(series, window);
            if (result is null) { complete = false; continue; }
            if (!collisions.TryGetValue(series.Identity.Fingerprint, out var ordinal)) ordinal = 0;
            collisions[series.Identity.Fingerprint] = checked(ordinal + 1);
            result.CollisionOrdinal = ordinal;
            result.WindowRowId = state.RowId;
            db.MetricRollupSeries.Add(result);
            complete &= !result.RequiresRaw;
        }
        state.ProjectedRevision = state.InputRevision;
        state.ProjectionVersion = ProjectionVersion;
        state.ProjectedAt = now;
        state.IsComplete = complete;
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        return complete ? MetricRollupStatus.Complete : MetricRollupStatus.Incomplete;
    }
}

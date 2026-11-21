using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application.HostMetrics;

/// <summary>Reads complete five-minute windows from rollups and clipped windows from raw samples.</summary>
internal static class HostMetricRollupQuery
{
    private static readonly long WindowTicks = TimeSpan.FromMinutes(5).Ticks;

    public static async Task<HostMetricAggregate> AggregateAsync(AgentManagementDbContext db, Guid agentId,
        DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, string source, string? dimension,
        int bucketSeconds, DateTimeOffset anchor, CancellationToken token)
    {
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'Vigiloom.HostMetricRollupBackfill',
                @LockMode = 'Shared', @LockOwner = 'Transaction', @LockTimeout = 5000;
            SELECT @result;
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0)
            throw new InvalidOperationException("Could not acquire the host metric history lock.");

        var startWindow = FloorWindow(from);
        var endWindow = FloorWindow(until);
        var fullStart = from == startWindow ? from :
            startWindow.Ticks <= DateTimeOffset.MaxValue.Ticks - WindowTicks ? startWindow.AddTicks(WindowTicks) : until;
        var fullEnd = endWindow;
        var raw = db.HostMetrics.AsNoTracking().Where(x => x.AgentId == agentId && x.Kind == kind &&
            EF.Functions.Collate(x.Source, "Latin1_General_100_BIN2") == source &&
            EF.Functions.DataLength(x.Source) == EF.Functions.DataLength(source) &&
            (dimension == null ? x.Dimension == null : x.Dimension != null &&
                EF.Functions.Collate(x.Dimension, "Latin1_General_100_BIN2") == dimension &&
                EF.Functions.DataLength(x.Dimension) == EF.Functions.DataLength(dimension)));
        var rollups = db.HostMetricRollups.AsNoTracking().Where(x => x.AgentId == agentId && x.Kind == kind &&
            EF.Functions.Collate(x.Source, "Latin1_General_100_BIN2") == source &&
            EF.Functions.DataLength(x.Source) == EF.Functions.DataLength(source) &&
            (dimension == null ? x.Dimension == null : x.Dimension != null &&
                EF.Functions.Collate(x.Dimension, "Latin1_General_100_BIN2") == dimension &&
                EF.Functions.DataLength(x.Dimension) == EF.Functions.DataLength(dimension)));

        if (bucketSeconds % 300 != 0)
        {
            // Five-minute summaries cannot be split accurately into arbitrary bucket sizes.
            var lastWindow = FloorWindow(until.AddTicks(-1));
            var end = lastWindow.Ticks <= DateTimeOffset.MaxValue.Ticks - WindowTicks
                ? lastWindow.AddTicks(WindowTicks) : DateTimeOffset.MaxValue;
            var expected = await rollups.Where(x => x.WindowStart >= startWindow && x.WindowStart <= lastWindow)
                .SumAsync(x => (long?)x.Count, token) ?? 0;
            var available = await raw.LongCountAsync(x => x.Aggregated && x.OccurredAt >= startWindow && x.OccurredAt < end, token);
            if (available < expected)
            {
                await tx.CommitAsync(token);
                return new(agentId, source, dimension, bucketSeconds, []) { HasIncompleteBuckets = true };
            }
            var rows = await raw.Where(x => x.OccurredAt >= from && x.OccurredAt < until)
                .GroupBy(x => EF.Functions.DateDiffSecond(anchor, x.OccurredAt) / bucketSeconds)
                .Select(g => new { Bucket = g.Key, Last = g.Max(x => x.OccurredAt), Min = g.Min(x => x.Value),
                    Max = g.Max(x => x.Value), Average = g.Average(x => x.Value), Count = g.LongCount() })
                .OrderBy(x => x.Bucket).ToArrayAsync(token);
            var rawBuckets = rows.Select(x => new HostMetricBucket(anchor.AddSeconds((long)x.Bucket * bucketSeconds),
                x.Last, x.Min, x.Max, x.Average, x.Count)).ToArray();
            await tx.CommitAsync(token);
            return new(agentId, source, dimension, bucketSeconds, rawBuckets);
        }

        var rawRows = await raw.Where(x => x.OccurredAt >= from && x.OccurredAt < until &&
                (!x.Aggregated || x.OccurredAt < fullStart || x.OccurredAt >= fullEnd))
            .GroupBy(x => EF.Functions.DateDiffSecond(anchor, x.OccurredAt) / bucketSeconds)
            .Select(g => new { Bucket = g.Key, Last = g.Max(x => x.OccurredAt), Min = g.Min(x => x.Value),
                Max = g.Max(x => x.Value), Sum = g.Sum(x => x.Value), Count = g.LongCount() }).ToArrayAsync(token);
        var rollupRows = await rollups.Where(x => x.WindowStart >= fullStart && x.WindowStart < fullEnd)
            .GroupBy(x => EF.Functions.DateDiffSecond(anchor, x.WindowStart) / bucketSeconds)
            .Select(g => new { Bucket = g.Key, Last = g.Max(x => x.LastOccurredAt), Min = g.Min(x => x.Minimum),
                Max = g.Max(x => x.Maximum), Sum = g.Sum(x => x.Sum), Count = g.Sum(x => x.Count) }).ToArrayAsync(token);

        // If an edge window has lost any aggregated raw samples, it cannot be sliced accurately.
        var edgeWindows = new HashSet<DateTimeOffset>();
        if (from != startWindow) edgeWindows.Add(startWindow);
        if (until != endWindow) edgeWindows.Add(endWindow);
        var incomplete = new HashSet<int>();
        foreach (var edge in edgeWindows)
        {
            var rollup = await rollups.Where(x => x.WindowStart == edge).Select(x => (long?)x.Count).SingleOrDefaultAsync(token);
            if (rollup is null) continue;
            var edgeEnd = edge.Ticks <= DateTimeOffset.MaxValue.Ticks - WindowTicks ? edge.AddTicks(WindowTicks) : DateTimeOffset.MaxValue;
            var remaining = await raw.LongCountAsync(x => x.Aggregated && x.OccurredAt >= edge && x.OccurredAt < edgeEnd, token);
            if (remaining < rollup.Value)
                incomplete.Add((int)((edge.ToUnixTimeSeconds() - anchor.ToUnixTimeSeconds()) / bucketSeconds));
        }

        var buckets = rawRows.Concat(rollupRows).GroupBy(x => x.Bucket).Where(g => !incomplete.Contains(g.Key))
            .OrderBy(g => g.Key).Select(g =>
            {
                var count = g.Sum(x => x.Count);
                return new HostMetricBucket(anchor.AddSeconds((long)g.Key * bucketSeconds), g.Max(x => x.Last),
                    g.Min(x => x.Min), g.Max(x => x.Max), g.Sum(x => x.Sum) / count, count);
            }).ToArray();
        await tx.CommitAsync(token);
        return new(agentId, source, dimension, bucketSeconds, buckets) { HasIncompleteBuckets = incomplete.Count != 0 };
    }

    private static DateTimeOffset FloorWindow(DateTimeOffset value)
        => new(value.Ticks - value.Ticks % WindowTicks, TimeSpan.Zero);
}

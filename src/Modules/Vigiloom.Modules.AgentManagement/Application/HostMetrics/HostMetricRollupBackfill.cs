using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application.HostMetrics;

/// <summary>Incrementally folds each Agent metric into its five-minute series exactly once.</summary>
public sealed class HostMetricRollupBackfill(IDbContextFactory<AgentManagementDbContext> contexts)
{
    public const int BatchSize = 100;

    public async Task<int> RunBatchAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = tx.GetDbTransaction();
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource = N'Vigiloom.HostMetricRollupBackfill',
                @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 0;
            SELECT @result;
            """;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token)) < 0) return 0;
        var points = await db.HostMetrics.FromSqlRaw("""
            SELECT TOP (100) * FROM [agent].[HostMetrics] WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
            WHERE [Aggregated] = 0 ORDER BY [RowId]
            """).ToArrayAsync(token);
        var groups = points.GroupBy(point => (point.AgentId, point.Kind, point.Source, point.Dimension, Start: WindowStart(point.OccurredAt)));
        foreach (var group in groups)
        {
            var (agentId, kind, source, dimension, start) = group.Key;
            var sourceBytes = Encoding.Unicode.GetBytes(source);
            var dimensionBytes = dimension is null ? null : Encoding.Unicode.GetBytes(dimension);
            var bucket = await db.HostMetricRollups.FromSqlInterpolated($"""
                SELECT * FROM [agent].[HostMetricRollups] WITH (UPDLOCK, HOLDLOCK)
                WHERE [AgentId] = {agentId} AND [Kind] = {(int)kind} AND [WindowStart] = {start}
                  AND [SourceBytes] = {sourceBytes} AND ([DimensionBytes] = {dimensionBytes} OR ([DimensionBytes] IS NULL AND {dimensionBytes} IS NULL))
                """).SingleOrDefaultAsync(token);
            if (bucket is null)
            {
                bucket = new HostMetricRollup(group.First(), start);
                db.HostMetricRollups.Add(bucket);
            }
            foreach (var point in group) bucket.Add(point);
        }
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        return points.Length;
    }

    private static DateTimeOffset WindowStart(DateTimeOffset value)
    {
        var ticks = value.ToUniversalTime().Ticks;
        return new(ticks - ticks % TimeSpan.FromMinutes(5).Ticks, TimeSpan.Zero);
    }
}

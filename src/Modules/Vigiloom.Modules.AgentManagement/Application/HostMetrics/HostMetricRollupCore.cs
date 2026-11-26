using System.Text;
using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application.HostMetrics;

internal static class HostMetricRollupCore
{
    internal static async Task<int> RunBatchAsync(DbContext db, CancellationToken token)
    {
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
        var points = await db.Set<HostMetric>().FromSqlRaw("""
            SELECT TOP (100) * FROM [agent].[HostMetrics] WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)
            WHERE [Aggregated] = 0 ORDER BY [RowId]
            """).ToArrayAsync(token);
        var groups = points.GroupBy(point => (point.AgentId, point.Kind, point.Source, point.Dimension, Start: WindowStart(point.OccurredAt))).ToArray();
        if (groups.Length == 0) return 0;
        var sql = new StringBuilder("SELECT * FROM [agent].[HostMetricRollups] WHERE ");
        var parameters = new List<object>(groups.Length * 5);
        for (var i = 0; i < groups.Length; i++)
        {
            if (i > 0) sql.Append(" OR ");
            var key = groups[i].Key;
            sql.Append($"([AgentId] = @agent{i} AND [Kind] = @kind{i} AND [WindowStart] = @start{i} AND [SourceBytes] = @source{i} AND ");
            parameters.Add(new SqlParameter($"@agent{i}", SqlDbType.UniqueIdentifier) { Value = key.AgentId });
            parameters.Add(new SqlParameter($"@kind{i}", SqlDbType.Int) { Value = (int)key.Kind });
            parameters.Add(new SqlParameter($"@start{i}", SqlDbType.DateTimeOffset) { Value = key.Start });
            parameters.Add(new SqlParameter($"@source{i}", SqlDbType.VarBinary, 256) { Value = Encoding.Unicode.GetBytes(key.Source) });
            if (key.Dimension is null) sql.Append("[DimensionBytes] IS NULL)");
            else
            {
                sql.Append($"[DimensionBytes] = @dimension{i})");
                parameters.Add(new SqlParameter($"@dimension{i}", SqlDbType.VarBinary, 512) { Value = Encoding.Unicode.GetBytes(key.Dimension) });
            }
        }
        // The transaction-owned application lock serializes all rollup writers.
        var existing = (await db.Set<HostMetricRollup>().FromSqlRaw(sql.ToString(), parameters.ToArray()).ToArrayAsync(token))
            .ToDictionary(x => (x.AgentId, x.Kind, x.Source, x.Dimension, Start: x.WindowStart));
        foreach (var group in groups)
        {
            if (!existing.TryGetValue(group.Key, out var bucket))
            {
                bucket = new HostMetricRollup(group.First(), group.Key.Start);
                db.Set<HostMetricRollup>().Add(bucket);
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

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Retention;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Retention;

/// <summary>Removes expired IOC and Sigma evidence in bounded batches.</summary>
public sealed class SecurityDetectionRetention : ISecurityDetectionRetention
{
    public async Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset now, int days, CancellationToken token)
    {
        var cutoff = now.AddDays(-days);
        var sigmaCutoff = now - (TimeSpan.FromDays(days) > SigmaCorrelationLimits.RequiredRetention ? TimeSpan.FromDays(days) : SigmaCorrelationLimits.RequiredRetention);
        await using var db = await OpenAsync(connection, transaction, token);
        return await db.IocDetections.LongCountAsync(x => x.ReceivedAt < cutoff, token) +
            await db.SigmaDetectionResults.LongCountAsync(x => x.ReceivedAt < sigmaCutoff, token) +
            await db.SigmaEventGaps.LongCountAsync(x => x.RecordedAt < cutoff, token);
    }

    public async Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset now, int days, int batchSize, CancellationToken token)
    {
        var cutoff = now.AddDays(-days);
        var sigmaCutoff = now - (TimeSpan.FromDays(days) > SigmaCorrelationLimits.RequiredRetention ? TimeSpan.FromDays(days) : SigmaCorrelationLimits.RequiredRetention);
        await using var db = await OpenAsync(connection, transaction, token);
        // Correlation evaluates and publishes under this same source lock.
        await db.IntelligenceSources.FromSqlRaw("SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = 'sigmahq-rules'")
            .AsNoTracking().SingleOrDefaultAsync(token);
        var iocIds = db.IocDetections.Where(x => x.ReceivedAt < cutoff).OrderBy(x => x.ReceivedAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize);
        var removed = await db.IocDetections.Where(x => iocIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
        if (removed < batchSize)
        {
            var sigmaIds = db.SigmaDetectionResults.Where(x => x.ReceivedAt < sigmaCutoff).OrderBy(x => x.ReceivedAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await db.SigmaDetectionResults.Where(x => sigmaIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        if (removed < batchSize)
        {
            var gapIds = db.SigmaEventGaps.Where(x => x.RecordedAt < cutoff).OrderBy(x => x.RecordedAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await db.SigmaEventGaps.Where(x => gapIds.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        return removed;
    }

    private static async Task<SecurityAnalysisDbContext> OpenAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(connection,
            sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        return db;
    }
}

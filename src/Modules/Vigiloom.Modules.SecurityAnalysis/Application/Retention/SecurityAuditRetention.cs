using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Baseline;
using Vigiloom.Modules.SecurityAnalysis.Domain.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Retention;

/// <summary>Applies the audit policy to security settings, change history and finished import staging data.</summary>
public sealed class SecurityAuditRetention : IAuditRetentionSource
{
    public async Task<long> CountAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        var previous = await IntelligenceContentRetention.ProtectedPreviousAsync(db, token);
        return await AuditRetentionQueries.CountAsync(db.InventorySettingAudits, cutoff, token) +
            await AuditRetentionQueries.CountAsync(db.IntegrityScopeAudits, cutoff, token) +
            await db.InventoryChanges.LongCountAsync(change => change.RecordedAt < cutoff, token) +
            await db.IntegrityChanges.LongCountAsync(change => change.RecordedAt < cutoff, token) +
            await OldEvaluations(db, cutoff).LongCountAsync(token) +
            await OldBaselineObservations(db, cutoff).LongCountAsync(token) +
            await IntelligenceContentRetention.CountAsync(db, cutoff, token) +
            await VendorContentRetention.CountAsync(db, cutoff, token) +
            await AdvisoryContentRetention.CountAsync(db, cutoff, previous, token) +
            await AdvisoryContentRetention.ReclaimableEntries(db, cutoff, previous).LongCountAsync(token) +
            await AdvisoryContentRetention.ReclaimablePages(db, cutoff, previous).LongCountAsync(token);
    }

    public async Task<int> DeleteBatchAsync(DbConnection connection, DbTransaction transaction, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        await using var db = await OpenAsync(connection, transaction, token);
        var removed = await AuditRetentionQueries.DeleteBatchAsync(db.InventorySettingAudits, cutoff, batchSize, token);
        if (removed < batchSize) removed += await AuditRetentionQueries.DeleteBatchAsync(db.IntegrityScopeAudits, cutoff, batchSize - removed, token);
        if (removed < batchSize)
        {
            var changes = db.InventoryChanges.Where(x => x.RecordedAt < cutoff).OrderBy(x => x.RecordedAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await db.InventoryChanges.Where(x => changes.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        if (removed < batchSize)
        {
            var changes = db.IntegrityChanges.Where(x => x.RecordedAt < cutoff).OrderBy(x => x.RecordedAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await db.IntegrityChanges.Where(x => changes.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        if (removed < batchSize)
        {
            var rows = OldEvaluations(db, cutoff).OrderBy(x => x.EvaluatedAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await OldEvaluations(db, cutoff).Where(x => rows.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        if (removed < batchSize)
        {
            var rows = OldBaselineObservations(db, cutoff).OrderBy(x => x.ReceivedAt).ThenBy(x => x.RowId).Select(x => x.RowId).Take(batchSize - removed);
            removed += await OldBaselineObservations(db, cutoff).Where(x => rows.Contains(x.RowId)).ExecuteDeleteAsync(token);
        }
        while (removed < batchSize)
        {
            var repositoryRows = await IntelligenceContentRetention.DeleteBatchAsync(db, cutoff, batchSize - removed, token);
            if (repositoryRows == 0) break;
            removed += repositoryRows;
        }
        while (removed < batchSize)
        {
            var vendorRows = await VendorContentRetention.DeleteBatchAsync(db, cutoff, batchSize - removed, token);
            if (vendorRows == 0) break;
            removed += vendorRows;
        }
        while (removed < batchSize)
        {
            var advisoryRows = await AdvisoryContentRetention.DeleteBatchAsync(db, cutoff, batchSize - removed, token);
            if (advisoryRows == 0) break;
            removed += advisoryRows;
        }
        if (removed < batchSize)
        {
            // The source lock is shared with import and abandonment, so references cannot change mid-batch.
            await db.IntelligenceSources.FromSqlRaw("SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Key] = 'github-reviewed'")
                .AsNoTracking().SingleOrDefaultAsync(token);
            var entries = db.AdvisoryImportEntries.Where(entry => db.AdvisoryImports.Any(import => import.Id == entry.ImportId && (import.Phase == 4 || import.Phase == 5) && import.StartedAt < cutoff) &&
                !db.AdvisoryRevisionEntries.Any(reference => reference.EntryRowId == entry.RowId)).OrderBy(entry => entry.RowId).Select(entry => entry.RowId).Take(batchSize - removed);
            removed += await db.AdvisoryImportEntries.Where(entry => entries.Contains(entry.RowId)).ExecuteDeleteAsync(token);
        }
        if (removed < batchSize)
        {
            var pages = db.AdvisoryImportPages.Where(page => db.AdvisoryImports.Any(import => import.Id == page.ImportId && (import.Phase == 4 || import.Phase == 5) && import.StartedAt < cutoff) &&
                !db.AdvisoryImportEntries.Any(entry => entry.PageRowId == page.RowId)).OrderBy(page => page.RowId).Select(page => page.RowId).Take(batchSize - removed);
            removed += await db.AdvisoryImportPages.Where(page => pages.Contains(page.RowId)).ExecuteDeleteAsync(token);
        }
        return removed;
    }

    private static IQueryable<VulnerabilityEvaluation> OldEvaluations(SecurityAnalysisDbContext db, DateTimeOffset cutoff) =>
        db.VulnerabilityEvaluations.Where(x => x.EvaluatedAt < cutoff && !db.VulnerabilityFindings.Any(f => f.Id == x.FindingId && f.LastRunId == x.RunId));

    private static IQueryable<BaselineObservation> OldBaselineObservations(SecurityAnalysisDbContext db, DateTimeOffset cutoff) =>
        db.BaselineObservations.Where(x => x.ReceivedAt < cutoff && db.BaselineObservations.Any(newer =>
            newer.DeviceId == x.DeviceId && newer.AgentId == x.AgentId && newer.DeviceGeneration == x.DeviceGeneration &&
            newer.SourceId == x.SourceId && newer.CheckId == x.CheckId && newer.RevisionId == x.RevisionId &&
            EF.Functions.Collate(newer.ActivationVersion, "Latin1_General_100_BIN2") == EF.Functions.Collate(x.ActivationVersion, "Latin1_General_100_BIN2") && (newer.ObservedAt > x.ObservedAt ||
                (newer.ObservedAt == x.ObservedAt && newer.RowId > x.RowId))));

    private static async Task<SecurityAnalysisDbContext> OpenAsync(DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(connection,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        return db;
    }
}

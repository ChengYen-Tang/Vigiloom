using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum EpssStageStatus { Staged, Unchanged, Conflict, Unavailable }
internal sealed record EpssStageResult(EpssStageStatus Status, Guid? RevisionId = null);

/// <summary>Stores a complete snapshot in bounded batches; incomplete stages cannot be activated.</summary>
internal sealed class EpssSnapshotStage(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    internal const string SourceKey = "first-epss";
    internal const string Mapping = "first-epss-csv-v1";
    internal const string Schema = "first-epss-v1";
    private const int BatchSize = 1000;
    private const int MaximumExpandedBytes = 128 * 1024 * 1024;

    internal async Task<EpssStageResult> StageAsync(Guid sourceId, byte[] expectedVersion, EpssClient client,
        string licenseEvidence, CancellationToken token)
    {
        if (sourceId == Guid.Empty || expectedVersion is not { Length: 8 } || string.IsNullOrWhiteSpace(licenseEvidence) || licenseEvidence.Length > 1024)
            throw new ArgumentException("A source, version and licensing basis are required.");
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, sourceId, token);
        if (lease is null) return new(EpssStageStatus.Conflict);
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId && x.Key == SourceKey, token);
        if (source is null || !source.Enabled) return new(EpssStageStatus.Unavailable);
        if (!source.Version.AsSpan().SequenceEqual(expectedVersion)) return new(EpssStageStatus.Conflict);
        var artifact = await client.FetchAsync(source.MaximumBytes, token);
        var revision = new IntelligenceRevision { SourceId = sourceId, UpstreamVersion = "staging", RetrievedAt = clock.GetUtcNow().ToUniversalTime(),
            Digest = RandomNumberGenerator.GetBytes(32), Artifact = artifact, SchemaVersion = Schema, MappingVersion = Mapping,
            LicenseEvidence = licenseEvidence, RecordCount = 0, Normalized = false };
        await using (var begin = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
        {
            var locked = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}").SingleOrDefaultAsync(token);
            if (locked is null || !locked.Enabled || locked.Key != SourceKey) return new(EpssStageStatus.Unavailable);
            if (!locked.Version.AsSpan().SequenceEqual(expectedVersion) || !await lease.VerifyHeldAsync(token)) return new(EpssStageStatus.Conflict);
            var abandoned = db.IntelligenceRevisions.Where(x => x.SourceId == sourceId && x.MappingVersion == Mapping && x.UpstreamVersion == "staging" && !x.Normalized && !x.Revoked &&
                !db.IntelligenceActivations.Any(a => a.RevisionId == x.Id) && !db.IntelligenceCaches.Any(c => c.RevisionId == x.Id) &&
                !db.IntelligenceActivationEvents.Any(e => e.RevisionId == x.Id || e.PreviousRevisionId == x.Id) &&
                !db.OfflineImports.Any(o => o.RevisionId == x.Id) && !db.AdvisoryImports.Any(i => i.BaseRevisionId == x.Id || i.ResultRevisionId == x.Id));
            await db.EpssScores.Where(x => abandoned.Select(r => r.Id).Contains(x.RevisionId)).ExecuteDeleteAsync(token);
            await abandoned.ExecuteDeleteAsync(token);
            db.IntelligenceRevisions.Add(revision);
            await db.SaveChangesAsync(token);
            await begin.CommitAsync(token);
        }
        db.ChangeTracker.Clear();
        using var batch = new DataTable();
        batch.Columns.Add("RevisionId", typeof(Guid));
        batch.Columns.Add("Cve", typeof(string));
        batch.Columns.Add("Probability", typeof(decimal));
        batch.Columns.Add("Percentile", typeof(decimal));
        EpssMetadata? metadata = null;
        using var compressed = new MemoryStream(artifact, writable: false);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var bounded = new EpssLimitedStream(gzip, MaximumExpandedBytes);
        var count = await EpssCsv.ReadAsync(bounded, clock.GetUtcNow(), source.MaximumRecords,
            (value, _) => { metadata = value; return ValueTask.CompletedTask; },
            async (value, cancellation) =>
            {
                batch.Rows.Add(revision.Id, value.Cve, value.Probability, value.Percentile);
                if (batch.Rows.Count == BatchSize) await FlushAsync(db, lease, batch, cancellation);
            }, token);
        bounded.ValidateFooter(artifact);
        await FlushAsync(db, lease, batch, token);
        var digest = SHA256.HashData(artifact);
        await using var final = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var current = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}").SingleOrDefaultAsync(token);
        if (current is null || !current.Enabled || current.Key != SourceKey) return new(EpssStageStatus.Unavailable);
        if (!current.Version.AsSpan().SequenceEqual(expectedVersion) || !await lease.VerifyHeldAsync(token)) return new(EpssStageStatus.Conflict);
        if (metadata is null || await db.EpssScores.CountAsync(x => x.RevisionId == revision.Id, token) != count) throw new InvalidDataException("EPSS snapshot is incomplete.");
        var priorCount = await db.IntelligenceRevisions.AsNoTracking().Where(x => x.SourceId == sourceId && x.MappingVersion == Mapping && x.Normalized && !x.Revoked)
            .OrderByDescending(x => x.RetrievedAt).Select(x => (int?)x.RecordCount).FirstOrDefaultAsync(token);
        if (priorCount.HasValue && (long)count * 2 < priorCount.Value) throw new InvalidDataException("EPSS snapshot unexpectedly shrank.");
        if (await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == digest && x.Revoked, token)) return new(EpssStageStatus.Unavailable);
        var existing = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId && x.Digest == digest && x.MappingVersion == Mapping && x.UpstreamVersion != "staging", token);
        if (existing is not null)
        {
            await db.EpssScores.Where(x => x.RevisionId == revision.Id).ExecuteDeleteAsync(token);
            await db.IntelligenceRevisions.Where(x => x.Id == revision.Id).ExecuteDeleteAsync(token);
            await final.CommitAsync(token);
            return existing.Revoked ? new(EpssStageStatus.Unavailable) : new(EpssStageStatus.Unchanged, existing.Id);
        }
        await db.IntelligenceRevisions.Where(x => x.Id == revision.Id).ExecuteUpdateAsync(x => x
            .SetProperty(r => r.Digest, digest).SetProperty(r => r.UpstreamVersion, metadata.ModelVersion)
            .SetProperty(r => r.ReleasedAt, metadata.ScoredAt).SetProperty(r => r.RecordCount, count).SetProperty(r => r.Normalized, true), token);
        await final.CommitAsync(token);
        return new(EpssStageStatus.Staged, revision.Id);
    }

    private static async Task FlushAsync(SecurityAnalysisDbContext db, IntelligenceRefreshLease lease, DataTable batch, CancellationToken token)
    {
        if (batch.Rows.Count == 0) return;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        if (!await lease.VerifyHeldAsync(token)) throw new InvalidOperationException("EPSS refresh lease was lost.");
        using var copy = new SqlBulkCopy((SqlConnection)db.Database.GetDbConnection(), SqlBulkCopyOptions.CheckConstraints, (SqlTransaction)transaction.GetDbTransaction())
        { DestinationTableName = "[security].[EpssScores]", BatchSize = BatchSize };
        foreach (DataColumn column in batch.Columns) copy.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await copy.WriteToServerAsync(batch, token);
        await transaction.CommitAsync(token);
        batch.Clear();
    }
}

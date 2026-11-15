using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum UbuntuStageStatus { Staged, Unchanged, Conflict, Unavailable }
internal sealed record UbuntuStageResult(UbuntuStageStatus Status, Guid? RevisionId = null);

/// <summary>Stages a complete Canonical archive; publication remains a separate operation.</summary>
internal sealed class UbuntuRevisionStage(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    internal const string SourceKey = "ubuntu-osv";
    internal const string MappingVersion = "ubuntu-osv-v1";
    internal const string SchemaVersion = "osv-1";
    internal const string LicenseEvidence = "Canonical Ubuntu Security Notices, CC BY-SA 4.0; https://github.com/canonical/ubuntu-security-notices/blob/main/LICENSE";
    private const long MaximumExpandedBytes = 32L * 1024 * 1024 * 1024;
    private const int MaximumDocumentBytes = 8 * 1024 * 1024;
    private const long MaximumScopes = 8_000_000;

    internal async Task<UbuntuStageResult> StageWithLeaseAsync(Guid sourceId, byte[] expectedVersion, byte[] artifact,
        IntelligenceRefreshLease lease, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(lease);
        if (sourceId == Guid.Empty || expectedVersion is not { Length: 8 } || !lease.IsFor(sourceId))
            throw new ArgumentException("A source, its version and lease are required.");
        var digest = SHA256.HashData(artifact);
        IntelligenceRevision revision;
        int maximumEntries;
        await using (var db = await contexts.CreateDbContextAsync(token))
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
        {
            var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
                .SingleOrDefaultAsync(token);
            if (source is null || source.Key != SourceKey || !source.Enabled) return new(UbuntuStageStatus.Unavailable);
            if (!source.Version.AsSpan().SequenceEqual(expectedVersion) || !await lease.VerifyHeldAsync(token)) return new(UbuntuStageStatus.Conflict);
            if (artifact.Length == 0 || artifact.Length > source.MaximumBytes) throw new InvalidDataException("Ubuntu archive exceeds its configured size limit.");
            maximumEntries = source.MaximumRecords;
            if (await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == digest && x.Revoked, token))
                return new(UbuntuStageStatus.Unavailable);
            var abandoned = db.IntelligenceRevisions.Where(x => x.SourceId == sourceId && x.MappingVersion == MappingVersion && !x.Normalized && !x.Revoked &&
                !db.IntelligenceActivations.Any(a => a.RevisionId == x.Id) && !db.IntelligenceCaches.Any(c => c.RevisionId == x.Id) &&
                !db.IntelligenceActivationEvents.Any(e => e.RevisionId == x.Id || e.PreviousRevisionId == x.Id) &&
                !db.OfflineImports.Any(o => o.RevisionId == x.Id) && !db.AdvisoryImports.Any(i => i.BaseRevisionId == x.Id || i.ResultRevisionId == x.Id));
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE #UbuntuAbandonedDocuments ([Digest] binary(32) NOT NULL, [MappingVersion] varchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
                    PRIMARY KEY ([Digest], [MappingVersion]));
                """, token);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO #UbuntuAbandonedDocuments ([Digest], [MappingVersion])
                SELECT DISTINCT m.[Digest], m.[MappingVersion]
                FROM [security].[UbuntuRevisionDocuments] AS m
                JOIN [security].[IntelligenceRevisions] AS r ON r.[Id] = m.[RevisionId]
                WHERE r.[SourceId] = {sourceId} AND r.[MappingVersion] = {MappingVersion} AND r.[Normalized] = 0 AND r.[Revoked] = 0
                  AND NOT EXISTS (SELECT 1 FROM [security].[IntelligenceActivations] AS a WHERE a.[RevisionId] = r.[Id])
                  AND NOT EXISTS (SELECT 1 FROM [security].[IntelligenceCaches] AS c WHERE c.[RevisionId] = r.[Id])
                  AND NOT EXISTS (SELECT 1 FROM [security].[IntelligenceActivationEvents] AS e WHERE e.[RevisionId] = r.[Id] OR e.[PreviousRevisionId] = r.[Id])
                  AND NOT EXISTS (SELECT 1 FROM [security].[OfflineImports] AS o WHERE o.[RevisionId] = r.[Id])
                  AND NOT EXISTS (SELECT 1 FROM [security].[AdvisoryImports] AS i WHERE i.[BaseRevisionId] = r.[Id] OR i.[ResultRevisionId] = r.[Id]);
                """, token);
            await db.UbuntuRevisionDocuments.Where(x => abandoned.Select(r => r.Id).Contains(x.RevisionId)).ExecuteDeleteAsync(token);
            await abandoned.ExecuteDeleteAsync(token);
            await db.Database.ExecuteSqlRawAsync("""
                DELETE s FROM [security].[UbuntuScopes] AS s
                JOIN #UbuntuAbandonedDocuments AS a ON a.[Digest] = s.[Digest] AND a.[MappingVersion] = s.[MappingVersion]
                WHERE NOT EXISTS (SELECT 1 FROM [security].[UbuntuRevisionDocuments] AS m WHERE m.[Digest] = s.[Digest] AND m.[MappingVersion] = s.[MappingVersion]);
                DELETE d FROM [security].[UbuntuDocuments] AS d
                JOIN #UbuntuAbandonedDocuments AS a ON a.[Digest] = d.[Digest] AND a.[MappingVersion] = d.[MappingVersion]
                WHERE NOT EXISTS (SELECT 1 FROM [security].[UbuntuRevisionDocuments] AS m WHERE m.[Digest] = d.[Digest] AND m.[MappingVersion] = d.[MappingVersion]);
                DROP TABLE #UbuntuAbandonedDocuments;
                """, token);
            var existing = await db.IntelligenceRevisions.Where(x => x.SourceId == sourceId && x.Digest == digest && x.MappingVersion == MappingVersion)
                .Select(x => new { x.Id, x.Normalized }).SingleOrDefaultAsync(token);
            if (existing is { Normalized: true })
            {
                await transaction.CommitAsync(token);
                return new(UbuntuStageStatus.Unchanged, existing.Id);
            }
            if (existing is not null) return new(UbuntuStageStatus.Unavailable);
            revision = new IntelligenceRevision { SourceId = sourceId, UpstreamVersion = Convert.ToHexStringLower(digest),
                RetrievedAt = clock.GetUtcNow().ToUniversalTime(), Digest = digest, Artifact = artifact, SchemaVersion = SchemaVersion,
                MappingVersion = MappingVersion, LicenseEvidence = LicenseEvidence, RecordCount = 0, Normalized = false };
            db.IntelligenceRevisions.Add(revision);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }

        var writer = await UbuntuRevisionWriter.OpenAsync(contexts, revision.Id, MappingVersion, token);
        using var archive = new MemoryStream(artifact, writable: false);
        var count = await UbuntuArchiveStaging.StageAsync(archive, writer, MaximumExpandedBytes, maximumEntries,
            MaximumDocumentBytes, MaximumScopes, token);
        await using var final = await contexts.CreateDbContextAsync(token);
        await using var commit = await final.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var current = await final.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (current is null || current.Key != SourceKey || !current.Enabled) return new(UbuntuStageStatus.Unavailable);
        if (!current.Version.AsSpan().SequenceEqual(expectedVersion) || !await lease.VerifyHeldAsync(token)) return new(UbuntuStageStatus.Conflict);
        var candidate = await final.IntelligenceRevisions.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceId] = {sourceId} AND [Id] = {revision.Id}")
            .Select(x => new { x.Id, x.Revoked, x.Normalized }).SingleOrDefaultAsync(token);
        if (candidate is null || candidate.Revoked || candidate.Normalized ||
            await final.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == digest && x.Revoked, token))
            return new(UbuntuStageStatus.Unavailable);
        if (await final.UbuntuRevisionDocuments.CountAsync(x => x.RevisionId == revision.Id, token) != count)
            throw new InvalidDataException("Ubuntu archive staged index is incomplete.");
        var latestAllowed = clock.GetUtcNow().Add(UbuntuRevisionEligibility.MaximumClockSkew);
        if (await (from member in final.UbuntuRevisionDocuments
            join document in final.UbuntuDocuments on new { member.Digest, member.MappingVersion, member.AdvisoryId }
                equals new { document.Digest, document.MappingVersion, document.AdvisoryId }
            where member.RevisionId == revision.Id && (document.ModifiedAt > latestAllowed || document.WithdrawnAt > latestAllowed)
            select document.RowId).AnyAsync(token)) throw new InvalidDataException("Ubuntu archive has timestamps in the future.");
        await final.IntelligenceRevisions.Where(x => x.Id == revision.Id).ExecuteUpdateAsync(x => x
            .SetProperty(r => r.RecordCount, count).SetProperty(r => r.Normalized, true), token);
        await commit.CommitAsync(token);
        return new(UbuntuStageStatus.Staged, revision.Id);
    }
}

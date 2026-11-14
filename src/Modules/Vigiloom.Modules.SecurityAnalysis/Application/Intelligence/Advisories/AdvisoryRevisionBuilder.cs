using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

internal enum AdvisoryBuildStatus { Staged, AlreadyStaged, Conflict, InvalidBase, LimitExceeded }
internal sealed record AdvisoryBuildResult(AdvisoryBuildStatus Status, Guid? RevisionId = null);

/// <summary>Creates an immutable revision from a reconciled window and its active base without activating it.</summary>
internal sealed class AdvisoryRevisionBuilder(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    internal const string MappingVersion = "ghsa-reviewed-v1";
    internal const string SchemaVersion = "github-advisories-2026-03-10";
    internal const string LicenseEvidence = "GitHub Advisory Database: CC-BY-4.0; github/advisory-database@09ff71573b6748a21637a08ffd9047b8a7da00cc";

    internal async Task<AdvisoryBuildResult> StageAsync(Guid sourceId, Guid importId, byte[] expectedVersion, CancellationToken token)
    {
        if (sourceId == Guid.Empty || importId == Guid.Empty || expectedVersion.Length != 8) return new(AdvisoryBuildStatus.Conflict);
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}").SingleOrDefaultAsync(token);
        if (source is null || source.Key != "github-reviewed") return new(AdvisoryBuildStatus.Conflict);
        var import = await db.AdvisoryImports.FromSqlInterpolated($"SELECT * FROM [security].[AdvisoryImports] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {importId} AND [SourceId] = {sourceId}").SingleOrDefaultAsync(token);
        if (import is null) return new(AdvisoryBuildStatus.Conflict);
        if (import.Phase == 4 && import.ResultRevisionId is { } existing) return new(AdvisoryBuildStatus.AlreadyStaged, existing);
        if (!source.Enabled || import.Phase != 3 || import.ReconciliationPhase != 3 ||
            !import.Version.AsSpan().SequenceEqual(expectedVersion) || !import.SourceVersion.AsSpan().SequenceEqual(source.Version)) return new(AdvisoryBuildStatus.Conflict);
        var active = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        if (active?.RevisionId != import.BaseRevisionId ||
            (active is null ? import.BaseActivationVersion is not null : !active.Version.AsSpan().SequenceEqual(import.BaseActivationVersion))) return new(AdvisoryBuildStatus.Conflict);

        if (active is not null && !await AdvisoryRevisionEligibility.IsEligibleAsync(db, sourceId, active.RevisionId, token))
            return new(AdvisoryBuildStatus.InvalidBase);

        var candidate = db.AdvisoryImportEntries.Where(x => x.ImportId == importId && !x.Reconciliation);
        if (await candidate.CountAsync(token) != import.RecordCount) return new(AdvisoryBuildStatus.Conflict);
        if (active is not null && await (from old in db.AdvisoryRevisionEntries
            join oldEntry in db.AdvisoryImportEntries on old.EntryRowId equals oldEntry.RowId
            join current in candidate on old.AdvisoryId equals current.AdvisoryId
            where old.RevisionId == active.RevisionId && oldEntry.UpdatedAt == current.UpdatedAt && oldEntry.Digest != current.Digest
            select old.RowId).AnyAsync(token)) return new(AdvisoryBuildStatus.Conflict);

        // The temporary digest is unique to this not-yet-visible transaction; it is replaced by the manifest digest before commit.
        var revision = new IntelligenceRevision
        {
            SourceId = sourceId, UpstreamVersion = $"window:{import.WindowStart:yyyy-MM-dd}..{import.WindowEnd:yyyy-MM-dd}:{import.Id:D}",
            ReleasedAt = null, RetrievedAt = clock.GetUtcNow(),
            Digest = SHA256.HashData(import.Id.ToByteArray()), Artifact = [], SchemaVersion = SchemaVersion,
            MappingVersion = MappingVersion, LicenseEvidence = LicenseEvidence
        };
        db.IntelligenceRevisions.Add(revision);
        await db.SaveChangesAsync(token);
        var baseId = active?.RevisionId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [security].[AdvisoryRevisionEntries] ([RevisionId], [EntryRowId], [AdvisoryId])
            SELECT {revision.Id}, old.[EntryRowId], old.[AdvisoryId]
            FROM [security].[AdvisoryRevisionEntries] old
            JOIN [security].[AdvisoryImportEntries] oldEntry ON oldEntry.[RowId] = old.[EntryRowId]
            LEFT JOIN [security].[AdvisoryImportEntries] incoming ON incoming.[ImportId] = {importId}
                AND incoming.[Reconciliation] = 0 AND incoming.[AdvisoryId] = old.[AdvisoryId]
            WHERE old.[RevisionId] = {baseId} AND (incoming.[RowId] IS NULL OR incoming.[UpdatedAt] <= oldEntry.[UpdatedAt])
            UNION ALL
            SELECT {revision.Id}, incoming.[RowId], incoming.[AdvisoryId]
            FROM [security].[AdvisoryImportEntries] incoming
            LEFT JOIN [security].[AdvisoryRevisionEntries] old ON old.[RevisionId] = {baseId} AND old.[AdvisoryId] = incoming.[AdvisoryId]
            LEFT JOIN [security].[AdvisoryImportEntries] oldEntry ON oldEntry.[RowId] = old.[EntryRowId]
            WHERE incoming.[ImportId] = {importId} AND incoming.[Reconciliation] = 0
                AND (old.[RowId] IS NULL OR incoming.[UpdatedAt] > oldEntry.[UpdatedAt])
            """, token);
        var count = await db.AdvisoryRevisionEntries.CountAsync(x => x.RevisionId == revision.Id, token);
        if (count > Math.Min(source.MaximumRecords, 100_000)) return new(AdvisoryBuildStatus.LimitExceeded);
        var contentHash = await HashMembersAsync(db, revision.Id, token);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "vigiloom-ghsa-snapshot-v1", schema = SchemaVersion, mapping = MappingVersion,
            importId, baseRevisionId = baseId, from = import.WindowStart, to = import.WindowEnd,
            recordCount = count, contentSha256 = Convert.ToHexStringLower(contentHash), coverage = "reviewed-window-not-point-in-time"
        });
        revision.RecordCount = count;
        revision.Artifact = manifest;
        revision.Digest = SHA256.HashData(manifest);
        revision.Normalized = true;
        import.Phase = 4;
        import.ResultRevisionId = revision.Id;
        source.LastCheckedAt = clock.GetUtcNow();
        source.LastSuccessfulCheckAt = source.LastCheckedAt;
        source.LastError = null;
        await db.SaveChangesAsync(token);
        import.ResultSourceVersion = source.Version.ToArray();
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(AdvisoryBuildStatus.Staged, revision.Id);
    }

    private static async Task<byte[]> HashMembersAsync(SecurityAnalysisDbContext db, Guid revisionId, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("vigiloom-ghsa-members-v1\0"u8);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT member.[AdvisoryId], entry.[Digest] FROM [security].[AdvisoryRevisionEntries] member JOIN [security].[AdvisoryImportEntries] entry ON entry.[RowId] = member.[EntryRowId] WHERE member.[RevisionId] = @revision ORDER BY member.[AdvisoryId] COLLATE Latin1_General_100_BIN2";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@revision";
        parameter.Value = revisionId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(token);
        var idBytes = new byte[19];
        var digestBytes = new byte[32];
        while (await reader.ReadAsync(token))
        {
            if (Encoding.ASCII.GetBytes(reader.GetString(0), idBytes) != idBytes.Length ||
                reader.GetBytes(1, 0, digestBytes, 0, digestBytes.Length) != digestBytes.Length)
                throw new InvalidDataException("Invalid advisory revision member.");
            hash.AppendData(idBytes);
            hash.AppendData(digestBytes);
        }
        return hash.GetHashAndReset();
    }
}

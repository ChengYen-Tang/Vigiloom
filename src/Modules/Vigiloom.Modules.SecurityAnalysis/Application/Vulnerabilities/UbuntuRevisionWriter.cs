using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal sealed record UbuntuPreparedDocument(string AdvisoryId, UbuntuOsvDocument Document);

/// <summary>Writes bounded batches to an unpublished revision. The source lease must be held by the caller.</summary>
internal sealed class UbuntuRevisionWriter
{
    internal const int MaximumBatchDocuments = 128;
    internal const int MaximumBatchScopes = 10_000;
    internal const int MaximumBatchBytes = 16 * 1024 * 1024;
    private const int MaximumKnownDocuments = 1_000_000;
    private readonly IDbContextFactory<SecurityAnalysisDbContext> contexts;
    private readonly Guid revisionId;
    private readonly string mappingVersion;
    private readonly HashSet<string> knownDigests;

    private UbuntuRevisionWriter(IDbContextFactory<SecurityAnalysisDbContext> contexts, Guid revisionId,
        string mappingVersion, HashSet<string> knownDigests)
        => (this.contexts, this.revisionId, this.mappingVersion, this.knownDigests) = (contexts, revisionId, mappingVersion, knownDigests);

    internal static async Task<UbuntuRevisionWriter> OpenAsync(IDbContextFactory<SecurityAnalysisDbContext> contexts,
        Guid revisionId, string mappingVersion, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        if (revisionId == Guid.Empty || string.IsNullOrWhiteSpace(mappingVersion) || mappingVersion.Length > 64)
            throw new ArgumentException("A revision and mapping version are required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        if (!await db.IntelligenceRevisions.AsNoTracking().AnyAsync(x => x.Id == revisionId && x.MappingVersion == mappingVersion && !x.Normalized && !x.Revoked, token))
            throw new InvalidOperationException("The staging revision is unavailable.");
        var digests = new HashSet<string>(StringComparer.Ordinal);
        await foreach (var digest in db.UbuntuDocuments.AsNoTracking().Where(x => x.MappingVersion == mappingVersion)
            .Select(x => x.Digest).Take(MaximumKnownDocuments + 1).AsAsyncEnumerable().WithCancellation(token))
        {
            digests.Add(Convert.ToHexString(digest));
            if (digests.Count > MaximumKnownDocuments) throw new InvalidDataException("Ubuntu document index exceeds its staging limit.");
        }
        return new(contexts, revisionId, mappingVersion, digests);
    }

    internal async Task WriteAsync(IReadOnlyList<UbuntuPreparedDocument> batch, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count is 0 or > MaximumBatchDocuments || batch.Sum(x => (long)x.Document.Scopes.Length) > MaximumBatchScopes ||
            batch.Sum(x => (long)x.Document.Compressed.Length) > MaximumBatchBytes ||
            batch.Any(x => x.Document.Compressed.Length == 0 || x.Document.OriginalLength is < 1 or > 8 * 1024 * 1024))
            throw new ArgumentException("Ubuntu staging batch exceeds its bounds.", nameof(batch));
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var revision = await db.IntelligenceRevisions.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {revisionId}")
            .Select(x => new { x.MappingVersion, x.Normalized, x.Revoked }).SingleOrDefaultAsync(token);
        if (revision is null || revision.Normalized || revision.Revoked || revision.MappingVersion != mappingVersion)
            throw new InvalidOperationException("The staging revision is unavailable.");
        var newDigests = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in batch)
        {
            var document = item.Document;
            if (string.IsNullOrWhiteSpace(item.AdvisoryId) || item.AdvisoryId.Length > 128 || document.Digest.Length != 32)
                throw new InvalidDataException("Invalid Ubuntu document identity.");
            var digest = Convert.ToHexString(document.Digest);
            if (!knownDigests.Contains(digest) && newDigests.Add(digest))
            {
                db.UbuntuDocuments.Add(new UbuntuDocument { Digest = document.Digest, MappingVersion = mappingVersion,
                    AdvisoryId = item.AdvisoryId, Compressed = document.Compressed, OriginalLength = document.OriginalLength,
                    ModifiedAt = document.ModifiedAt, WithdrawnAt = document.WithdrawnAt });
                foreach (var scope in document.Scopes)
                    db.UbuntuScopes.Add(new UbuntuScope { Digest = document.Digest, MappingVersion = mappingVersion,
                        Ecosystem = scope.Ecosystem, SourcePackage = scope.SourcePackage });
            }
            db.UbuntuRevisionDocuments.Add(new UbuntuRevisionDocument { RevisionId = revisionId, Digest = document.Digest,
                MappingVersion = mappingVersion, AdvisoryId = item.AdvisoryId });
        }
        if (knownDigests.Count + newDigests.Count > MaximumKnownDocuments)
            throw new InvalidDataException("Ubuntu document index exceeds its staging limit.");
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        knownDigests.UnionWith(newDigests);
    }
}

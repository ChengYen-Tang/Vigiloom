using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum MsrcStageStatus { Staged, Unchanged, Conflict, Unavailable }
internal sealed record MsrcStageResult(MsrcStageStatus Status, Guid? RevisionId = null);

/// <summary>Stages a bounded, catalog-consistent set without activating it or asserting matcher completeness.</summary>
internal sealed class MsrcSnapshotStage(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    internal const string SourceKey = "msrc-cvrf";
    internal const string Mapping = "msrc-cvrf-index-v2";
    internal const string Schema = "msrc-cvrf-v3";
    private const int MaximumDocumentBytes = 64 * 1024 * 1024;
    private const int MaximumDocuments = 2000;

    internal async Task<MsrcStageResult> StageAsync(Guid sourceId, byte[] expectedVersion, MsrcDocumentClient client,
        string licenseEvidence, CancellationToken token)
    {
        if (sourceId == Guid.Empty || expectedVersion is not { Length: 8 } || string.IsNullOrWhiteSpace(licenseEvidence) || licenseEvidence.Length > 1024)
            throw new ArgumentException("A source, version and licensing basis are required.");
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, sourceId, token);
        if (lease is null) return new(MsrcStageStatus.Conflict);
        var catalog = await client.GetCatalogAsync(4 * 1024 * 1024, MaximumDocuments, token);
        var ordered = catalog.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var manifest = JsonSerializer.SerializeToUtf8Bytes(ordered);
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId && x.Key == SourceKey, token);
        if (source is null || !source.Enabled) return new(MsrcStageStatus.Unavailable);
        if (!source.Version.AsSpan().SequenceEqual(expectedVersion)) return new(MsrcStageStatus.Conflict);
        var revision = new IntelligenceRevision { SourceId = sourceId, UpstreamVersion = "staging", RetrievedAt = clock.GetUtcNow().ToUniversalTime(),
            Digest = RandomNumberGenerator.GetBytes(32), Artifact = manifest, SchemaVersion = Schema, MappingVersion = Mapping,
            LicenseEvidence = licenseEvidence, RecordCount = ordered.Length, Normalized = false };
        await using (var begin = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
        {
            var locked = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}").SingleOrDefaultAsync(token);
            if (locked is null || !locked.Enabled || locked.Key != SourceKey) return new(MsrcStageStatus.Unavailable);
            if (!locked.Version.AsSpan().SequenceEqual(expectedVersion) || !await lease.VerifyHeldAsync(token)) return new(MsrcStageStatus.Conflict);
            var abandoned = db.IntelligenceRevisions.Where(x => x.SourceId == sourceId && x.MappingVersion == Mapping && x.UpstreamVersion == "staging" && !x.Normalized && !x.Revoked &&
                !db.IntelligenceActivations.Any(a => a.RevisionId == x.Id) && !db.IntelligenceCaches.Any(c => c.RevisionId == x.Id) &&
                !db.IntelligenceActivationEvents.Any(e => e.RevisionId == x.Id || e.PreviousRevisionId == x.Id) &&
                !db.OfflineImports.Any(o => o.RevisionId == x.Id) && !db.AdvisoryImports.Any(i => i.BaseRevisionId == x.Id || i.ResultRevisionId == x.Id));
            await db.MsrcDocuments.Where(x => abandoned.Select(r => r.Id).Contains(x.RevisionId)).ExecuteDeleteAsync(token);
            await abandoned.ExecuteDeleteAsync(token);
            await db.MsrcArtifacts.Where(x => !db.MsrcDocuments.Any(d => d.Digest == x.Digest)).ExecuteDeleteAsync(token);
            db.IntelligenceRevisions.Add(revision);
            await db.SaveChangesAsync(token);
            await begin.CommitAsync(token);
        }
        db.ChangeTracker.Clear();
        long totalBytes = manifest.Length;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(manifest);
        foreach (var entry in ordered)
        {
            token.ThrowIfCancellationRequested();
            if (!await lease.VerifyHeldAsync(token)) return new(MsrcStageStatus.Conflict);
            var (artifact, document) = await client.FetchAsync(entry.Id, Math.Min(source.MaximumBytes, MaximumDocumentBytes), source.MaximumRecords, token);
            if (document.ModifiedAt != entry.ModifiedAt)
            {
                // Observed upstream: catalog 1753 and document 0001 for an unspecified date.
                // Two equal responses do not establish freshness or vulnerability coverage.
                if (entry.ModifiedAt != new DateTimeOffset(1753, 1, 1, 0, 0, 0, TimeSpan.Zero) || document.ModifiedAt != DateTimeOffset.MinValue)
                    throw new InvalidDataException($"MSRC document {entry.Id} does not match the catalog version.");
                var (confirmation, _) = await client.FetchAsync(entry.Id, Math.Min(source.MaximumBytes, MaximumDocumentBytes), source.MaximumRecords, token);
                if (!SHA256.HashData(artifact).AsSpan().SequenceEqual(SHA256.HashData(confirmation)))
                    throw new InvalidDataException($"MSRC document {entry.Id} changed while being retrieved.");
            }
            totalBytes += artifact.Length;
            if (totalBytes > source.MaximumBytes) throw new InvalidDataException("MSRC snapshot exceeds its total size limit.");
            var hash = SHA256.HashData(artifact);
            digest.AppendData(hash);
            await using (var write = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
            {
                if (!await lease.VerifyHeldAsync(token)) return new(MsrcStageStatus.Conflict);
                var indexedCves = document.Advisories.Select(x => x.Cve).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var retained = await db.MsrcArtifacts.SingleOrDefaultAsync(x => x.Digest == hash, token);
                if (retained is not null && !SHA256.HashData(retained.Content).AsSpan().SequenceEqual(hash))
                    throw new InvalidDataException("Retained MSRC document does not match its digest.");
                if (retained is null)
                {
                    retained = new MsrcArtifact { Digest = hash, Content = artifact, IndexedCveCount = indexedCves.Length };
                    db.MsrcArtifacts.Add(retained);
                    db.MsrcCveIndexes.AddRange(indexedCves.Select(cve => new MsrcCveIndex { Digest = hash, Cve = cve }));
                }
                else if (retained.IndexedCveCount == -1)
                {
                    if (await db.MsrcCveIndexes.AnyAsync(x => x.Digest == hash, token)) throw new InvalidDataException("Incomplete MSRC candidate index.");
                    retained.IndexedCveCount = indexedCves.Length;
                    db.MsrcCveIndexes.AddRange(indexedCves.Select(cve => new MsrcCveIndex { Digest = hash, Cve = cve }));
                }
                else
                {
                    var existingCves = await db.MsrcCveIndexes.AsNoTracking().Where(x => x.Digest == hash).Select(x => x.Cve).ToArrayAsync(token);
                    if (retained.IndexedCveCount != indexedCves.Length ||
                        !existingCves.Order(StringComparer.Ordinal).SequenceEqual(indexedCves, StringComparer.Ordinal))
                        throw new InvalidDataException("MSRC candidate index does not match its retained document.");
                }
                var productDiscovery = MsrcProductDiscovery.Discover(document);
                var productRows = productDiscovery.Candidates.Select(x => new MsrcProductIndex
                {
                    Digest = hash,
                    ProductHash = SHA256.HashData(Encoding.UTF8.GetBytes(x.ProductName)),
                    ProductName = x.ProductName,
                    Cve = x.Cve
                }).ToArray();
                if (retained.IndexedProductCount == -1)
                {
                    if (await db.MsrcProductIndexes.AnyAsync(x => x.Digest == hash, token)) throw new InvalidDataException("Incomplete MSRC product index.");
                    retained.IndexedProductCount = productRows.Length;
                    retained.ProductIndexComplete = productDiscovery.Complete;
                    db.MsrcProductIndexes.AddRange(productRows);
                }
                else
                {
                    var existingProducts = await db.MsrcProductIndexes.AsNoTracking().Where(x => x.Digest == hash)
                        .Select(x => new { x.ProductName, x.ProductHash, x.Cve }).ToArrayAsync(token);
                    if (retained.IndexedProductCount != productRows.Length || retained.ProductIndexComplete != productDiscovery.Complete ||
                        existingProducts.Any(x => !SHA256.HashData(Encoding.UTF8.GetBytes(x.ProductName)).AsSpan().SequenceEqual(x.ProductHash)) ||
                        !existingProducts.Select(x => new MsrcProductCandidate(x.ProductName, x.Cve)).ToHashSet().SetEquals(productDiscovery.Candidates))
                        throw new InvalidDataException("MSRC product index does not match its retained document.");
                }
                db.MsrcDocuments.Add(new Domain.Intelligence.MsrcDocument { RevisionId = revision.Id, DocumentId = entry.Id, ModifiedAt = entry.ModifiedAt, Digest = hash });
                await db.SaveChangesAsync(token);
                await write.CommitAsync(token);
            }
            db.ChangeTracker.Clear();
        }
        var after = (await client.GetCatalogAsync(4 * 1024 * 1024, MaximumDocuments, token)).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        if (!ordered.SequenceEqual(after)) return new(MsrcStageStatus.Conflict);
        var finalDigest = digest.GetHashAndReset();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var current = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}").SingleOrDefaultAsync(token);
        if (current is null || !current.Enabled || current.Key != SourceKey) return new(MsrcStageStatus.Unavailable);
        if (!current.Version.AsSpan().SequenceEqual(expectedVersion) || !await lease.VerifyHeldAsync(token)) return new(MsrcStageStatus.Conflict);
        if (await db.MsrcDocuments.CountAsync(x => x.RevisionId == revision.Id, token) != ordered.Length) throw new InvalidDataException("MSRC snapshot is incomplete.");
        if (await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == finalDigest && x.Revoked, token))
        {
            await db.MsrcDocuments.Where(x => x.RevisionId == revision.Id).ExecuteDeleteAsync(token);
            await db.IntelligenceRevisions.Where(x => x.Id == revision.Id).ExecuteDeleteAsync(token);
            await db.MsrcArtifacts.Where(x => !db.MsrcDocuments.Any(d => d.Digest == x.Digest)).ExecuteDeleteAsync(token);
            await transaction.CommitAsync(token);
            return new(MsrcStageStatus.Unavailable);
        }
        var existing = await db.IntelligenceRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId && x.Digest == finalDigest && x.MappingVersion == Mapping && x.UpstreamVersion != "staging", token);
        if (existing is not null)
        {
            await db.MsrcDocuments.Where(x => x.RevisionId == revision.Id).ExecuteDeleteAsync(token);
            await db.IntelligenceRevisions.Where(x => x.Id == revision.Id).ExecuteDeleteAsync(token);
            await db.MsrcArtifacts.Where(x => !db.MsrcDocuments.Any(d => d.Digest == x.Digest)).ExecuteDeleteAsync(token);
            await transaction.CommitAsync(token);
            return existing.Revoked ? new(MsrcStageStatus.Unavailable) : new(MsrcStageStatus.Unchanged, existing.Id);
        }
        await db.IntelligenceRevisions.Where(x => x.Id == revision.Id).ExecuteUpdateAsync(x => x
            .SetProperty(r => r.Digest, finalDigest).SetProperty(r => r.UpstreamVersion, Convert.ToHexStringLower(finalDigest))
            .SetProperty(r => r.Normalized, true), token);
        await transaction.CommitAsync(token);
        return new(MsrcStageStatus.Staged, revision.Id);
    }
}

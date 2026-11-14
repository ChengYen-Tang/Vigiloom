using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Cryptography;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>Stages a verified KEV revision without changing the active revision.</summary>
internal sealed class KevUpdate(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory? clients, TimeProvider clock)
{
    internal const string LicenseEvidence = "CISA KEV: CC0-1.0; cisagov/kev-data@a795af4c4afc47df12906b4c04e656156cf06493";
    internal const string MappingVersion = "kev-cve-v1";
    internal const string OfflineMappingVersion = "kev-cve-offline-v1";
    internal const string OfflineLicenseEvidence = "Administrator-provided KEV-format snapshot; upstream provenance and license are not verified.";
    internal async Task<KevUpdateResult> RunAsync(Guid sourceId, CancellationToken token, bool onlyWhenDue = false)
    {
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, sourceId, token);
        if (lease is null) return KevUpdateResult.Busy;
        IntelligenceSource? source;
        IntelligenceCache? cached;
        await using (var read = await contexts.CreateDbContextAsync(token))
        {
            source = await read.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId, token);
            if (source is null || !source.Enabled || source.Key != "cisa-kev") return KevUpdateResult.Unavailable;
            if (onlyWhenDue && !IntelligenceRefresh.IsDue(source, clock.GetUtcNow())) return KevUpdateResult.NotDue;
            cached = await (from cache in read.IntelligenceCaches.AsNoTracking()
                            join revision in read.IntelligenceRevisions.AsNoTracking()
                                on new { cache.SourceId, Id = cache.RevisionId } equals new { revision.SourceId, revision.Id }
                            where cache.SourceId == source.Id && !revision.Revoked && revision.Normalized && revision.MappingVersion == MappingVersion &&
                                !read.IntelligenceRevisions.Any(x => x.SourceId == revision.SourceId && x.Digest == revision.Digest && x.Revoked)
                            select cache).SingleOrDefaultAsync(token);
        }
        var etag = cached?.ETag;
        var modified = cached?.LastModified;
        KevDownload download;
        KevCatalogMetadata? metadata = null;
        try
        {
            using var client = (clients ?? throw new InvalidOperationException("HTTP client factory is required for online updates.")).CreateClient(IntelligenceHttp.KevClientName);
            download = await new KevCatalogClient(client).FetchAsync(etag, modified, source.MaximumBytes, token);
            if (download.ETag is { Length: > 512 }) throw new InvalidDataException("KEV response ETag is too long.");
            if (download.Content is not null)
                metadata = KevCatalogValidator.Validate(download.Content, source.MaximumBytes, source.MaximumRecords);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException && !token.IsCancellationRequested)
        {
            var reason = ex switch
            {
                InvalidDataException => "KEV content or response failed validation.",
                OperationCanceledException => "KEV retrieval timed out.",
                _ => "KEV retrieval failed."
            };
            await RecordFailureAsync(source, reason, token);
            return KevUpdateResult.Failed;
        }

        return await PersistAsync(source, download, metadata, cached, null, null, token);
    }

    private async Task<KevUpdateResult> PersistAsync(IntelligenceSource source, KevDownload download, KevCatalogMetadata? metadata,
        IntelligenceCache? cached, Guid? offlineActor, IOperationAuditWriter<OperationAudit>? audit, CancellationToken token)
    {
        var sourceId = source.Id;
        var mappingVersion = offlineActor is null ? MappingVersion : OfflineMappingVersion;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var current = await db.IntelligenceSources
            .FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (current is null || !current.Enabled || !current.Version.AsSpan().SequenceEqual(source.Version)) return KevUpdateResult.Conflict;
        var currentCache = await db.IntelligenceCaches.SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        if (download.Content is null && (cached is null || currentCache is null || currentCache.RevisionId != cached.RevisionId ||
            currentCache.ETag != cached.ETag || currentCache.LastModified != cached.LastModified ||
            !await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Id == cached.RevisionId && !x.Revoked && x.Normalized && x.MappingVersion == MappingVersion &&
                !db.IntelligenceRevisions.Any(other => other.SourceId == x.SourceId && other.Digest == x.Digest && other.Revoked), token)))
            return KevUpdateResult.Conflict;
        var existing = download.Digest is null ? null : await db.IntelligenceRevisions.AsNoTracking()
            .Where(x => x.SourceId == sourceId && x.Digest == download.Digest && x.MappingVersion == mappingVersion)
            .Select(x => new { x.Id, x.Revoked, x.Normalized }).SingleOrDefaultAsync(token);
        var contentRevoked = download.Digest is not null && await db.IntelligenceRevisions.AsNoTracking()
            .AnyAsync(x => x.SourceId == sourceId && x.Digest == download.Digest && x.Revoked, token);
        if (contentRevoked || existing is { Normalized: false })
        {
            if (offlineActor is not null) return KevUpdateResult.Failed;
            current.LastCheckedAt = clock.GetUtcNow();
            current.LastError = "The retrieved KEV revision is revoked or incomplete.";
            db.Entry(current).Property(x => x.LastCheckedAt).IsModified = true;
            try { await db.SaveChangesAsync(token); await transaction.CommitAsync(token); }
            catch (DbUpdateConcurrencyException) { return KevUpdateResult.Conflict; }
            return KevUpdateResult.Failed;
        }
        Guid? existingId = existing?.Id;
        var revisionExists = existingId is not null;
        if (download.Content is not null && metadata is not null && existingId is null)
        {
            var revision = new IntelligenceRevision
            {
                SourceId = sourceId, UpstreamVersion = metadata.Version, ReleasedAt = metadata.ReleasedAt,
                RetrievedAt = clock.GetUtcNow(), Digest = download.Digest!, Artifact = download.Content,
                SchemaVersion = KevCatalogValidator.SchemaRevision,
                MappingVersion = mappingVersion,
                LicenseEvidence = offlineActor is null ? LicenseEvidence : OfflineLicenseEvidence,
                RecordCount = metadata.RecordCount, Normalized = true
            };
            db.IntelligenceRevisions.Add(revision);
            existingId = revision.Id;
        }
        if (download.Content is not null && offlineActor is null)
        {
            if (currentCache is null)
            {
                currentCache = new IntelligenceCache { SourceId = sourceId };
                db.IntelligenceCaches.Add(currentCache);
            }
            currentCache.RevisionId = existingId!.Value;
        }
        if (currentCache is not null && offlineActor is null)
        {
            currentCache.ETag = download.ETag;
            currentCache.LastModified = download.LastModified;
        }
        if (offlineActor is null)
        {
            current.LastCheckedAt = clock.GetUtcNow();
            current.LastSuccessfulCheckAt = current.LastCheckedAt;
            current.LastError = null;
            db.Entry(current).Property(x => x.LastCheckedAt).IsModified = true;
        }
        try
        {
            await db.SaveChangesAsync(token);
            if (download.Content is not null && !revisionExists)
            {
                db.ChangeTracker.Clear();
                var pending = 0;
                var written = 0;
                foreach (var record in KevNormalizer.Read(download.Content, existingId!.Value))
                {
                    db.KevRecords.Add(record);
                    written++;
                    if (++pending < 256) continue;
                    await db.SaveChangesAsync(token);
                    db.ChangeTracker.Clear();
                    pending = 0;
                }
                if (pending != 0) await db.SaveChangesAsync(token);
                if (written != metadata!.RecordCount) throw new InvalidDataException("Normalized KEV count does not match the validated catalog.");
            }
            if (offlineActor is { } actor)
            {
                var importedAt = clock.GetUtcNow();
                db.OfflineImports.Add(new OfflineImport { SourceId = sourceId, RevisionId = existingId!.Value, ActorId = actor, ImportedAt = importedAt });
                await db.SaveChangesAsync(token);
                await audit!.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
                    new OperationAudit(actor, sourceId, importedAt, new IntelligenceOfflineImported(existingId.Value)), token);
            }
            await transaction.CommitAsync(token);
        }
        catch (DbUpdateConcurrencyException) { return KevUpdateResult.Conflict; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return KevUpdateResult.Conflict; }
        return download.Content is null || revisionExists ? KevUpdateResult.Unchanged : KevUpdateResult.Staged;
    }

    internal async Task<KevUpdateResult> ImportAsync(Guid sourceId, byte[] content, Guid actor,
        IOperationAuditWriter<OperationAudit> audit, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId, token);
        if (source is null || !source.Enabled || source.Key != "cisa-kev") return KevUpdateResult.Unavailable;
        KevCatalogMetadata metadata;
        try { metadata = KevCatalogValidator.Validate(content, source.MaximumBytes, source.MaximumRecords); }
        catch (InvalidDataException) { return KevUpdateResult.Failed; }
        return await PersistAsync(source, new KevDownload(content, SHA256.HashData(content), null, null), metadata, null, actor, audit, token);
    }

    private async Task RecordFailureAsync(IntelligenceSource source, string reason, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var current = await db.IntelligenceSources.SingleOrDefaultAsync(x => x.Id == source.Id, token);
        if (current is null || !current.Version.AsSpan().SequenceEqual(source.Version)) return;
        current.LastCheckedAt = clock.GetUtcNow();
        current.LastError = reason;
        db.Entry(current).Property(x => x.LastCheckedAt).IsModified = true;
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateConcurrencyException) { }
    }
}

internal enum KevUpdateResult { Unavailable, Failed, Conflict, Unchanged, Staged, Busy, NotDue }

using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using System.Data;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

/// <summary>Stages a verified KEV revision without changing the active revision.</summary>
internal sealed class KevUpdate(IDbContextFactory<SecurityAnalysisDbContext> contexts, IHttpClientFactory clients, TimeProvider clock)
{
    internal async Task<KevUpdateResult> RunAsync(Guid sourceId, CancellationToken token)
    {
        IntelligenceSource? source;
        IntelligenceCache? cached;
        await using (var read = await contexts.CreateDbContextAsync(token))
        {
            source = await read.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId, token);
            if (source is null || !source.Enabled || source.Key != "cisa-kev") return KevUpdateResult.Unavailable;
            cached = await (from cache in read.IntelligenceCaches.AsNoTracking()
                            join revision in read.IntelligenceRevisions.AsNoTracking()
                                on new { cache.SourceId, Id = cache.RevisionId } equals new { revision.SourceId, revision.Id }
                            where cache.SourceId == source.Id && !revision.Revoked
                            select cache).SingleOrDefaultAsync(token);
        }
        var etag = cached?.ETag;
        var modified = cached?.LastModified;
        KevDownload download;
        KevCatalogMetadata? metadata = null;
        try
        {
            using var client = clients.CreateClient(IntelligenceHttp.KevClientName);
            download = await new KevCatalogClient(client).FetchAsync(etag, modified, KevCatalogValidator.DefaultMaximumBytes, token);
            if (download.ETag is { Length: > 512 }) throw new InvalidDataException("KEV response ETag is too long.");
            if (download.Content is not null)
                metadata = KevCatalogValidator.Validate(download.Content, KevCatalogValidator.DefaultMaximumBytes, KevCatalogValidator.DefaultMaximumRecords);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or OperationCanceledException && !token.IsCancellationRequested)
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

        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var current = await db.IntelligenceSources
            .FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (current is null || !current.Enabled || !current.Version.AsSpan().SequenceEqual(source.Version)) return KevUpdateResult.Conflict;
        var currentCache = await db.IntelligenceCaches.SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        if (download.Content is null && (cached is null || currentCache is null || currentCache.RevisionId != cached.RevisionId ||
            currentCache.ETag != cached.ETag || currentCache.LastModified != cached.LastModified ||
            !await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Id == cached.RevisionId && !x.Revoked, token)))
            return KevUpdateResult.Conflict;
        var existing = download.Digest is null ? null : await db.IntelligenceRevisions.AsNoTracking()
            .Where(x => x.SourceId == sourceId && x.Digest == download.Digest)
            .Select(x => new { x.Id, x.Revoked }).SingleOrDefaultAsync(token);
        if (existing?.Revoked == true)
        {
            current.LastCheckedAt = clock.GetUtcNow();
            current.LastError = "The retrieved KEV revision was revoked.";
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
                MappingVersion = "kev-raw-v1",
                LicenseEvidence = "CISA KEV: CC0-1.0; cisagov/kev-data@a795af4c4afc47df12906b4c04e656156cf06493",
                RecordCount = metadata.RecordCount
            };
            db.IntelligenceRevisions.Add(revision);
            existingId = revision.Id;
        }
        if (download.Content is not null)
        {
            if (currentCache is null)
            {
                currentCache = new IntelligenceCache { SourceId = sourceId };
                db.IntelligenceCaches.Add(currentCache);
            }
            currentCache.RevisionId = existingId!.Value;
        }
        if (currentCache is not null)
        {
            currentCache.ETag = download.ETag;
            currentCache.LastModified = download.LastModified;
        }
        current.LastCheckedAt = clock.GetUtcNow();
        current.LastSuccessfulCheckAt = current.LastCheckedAt;
        current.LastError = null;
        db.Entry(current).Property(x => x.LastCheckedAt).IsModified = true;
        try { await db.SaveChangesAsync(token); await transaction.CommitAsync(token); }
        catch (DbUpdateConcurrencyException) { return KevUpdateResult.Conflict; }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return KevUpdateResult.Conflict; }
        return download.Content is null || revisionExists ? KevUpdateResult.Unchanged : KevUpdateResult.Staged;
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

internal enum KevUpdateResult { Unavailable, Failed, Conflict, Unchanged, Staged }

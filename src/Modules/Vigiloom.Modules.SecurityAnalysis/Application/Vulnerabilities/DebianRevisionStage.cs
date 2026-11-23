using System.Data;
using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum DebianStageStatus { Staged, Unchanged, Conflict, Unavailable, Busy }
internal sealed record DebianStageResult(DebianStageStatus Status, Guid? RevisionId = null);

/// <summary>Stages one fully validated tracker snapshot without selecting it as the active source.</summary>
internal sealed class DebianRevisionStage(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    internal const string SourceKey = "debian-security";
    internal const string SchemaVersion = "debian-tracker-json-v1";
    internal const string MappingVersion = "debian-release-v1";
    private const int BatchSize = 256;

    internal async Task<DebianStageResult> StageAsync(Guid sourceId, byte[] expectedSourceVersion, byte[] content,
        string licenseEvidence, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (sourceId == Guid.Empty || expectedSourceVersion is not { Length: 8 } || string.IsNullOrWhiteSpace(licenseEvidence) || licenseEvidence.Length > 1024)
            throw new ArgumentException("A source, version and documented licensing basis are required.");
        await using var lease = await IntelligenceRefreshLease.TryAcquireAsync(contexts, sourceId, token);
        if (lease is null) return new(DebianStageStatus.Busy);
        return await StageWithLeaseAsync(sourceId, expectedSourceVersion, content, licenseEvidence, lease, token);
    }

    internal async Task<DebianStageResult> StageWithLeaseAsync(Guid sourceId, byte[] expectedSourceVersion, byte[] content,
        string licenseEvidence, IntelligenceRefreshLease lease, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(lease);
        if (!lease.IsFor(sourceId)) throw new ArgumentException("Lease does not belong to the source.", nameof(lease));
        if (sourceId == Guid.Empty || expectedSourceVersion is not { Length: 8 } || string.IsNullOrWhiteSpace(licenseEvidence) || licenseEvidence.Length > 1024)
            throw new ArgumentException("A source, version and documented licensing basis are required.");
        int maximumBytes, maximumRecords;
        await using (var read = await contexts.CreateDbContextAsync(token))
        {
            var source = await read.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId && x.Key == SourceKey, token);
            if (source is null || !source.Enabled) return new(DebianStageStatus.Unavailable);
            if (!source.Version.AsSpan().SequenceEqual(expectedSourceVersion)) return new(DebianStageStatus.Conflict);
            maximumBytes = source.MaximumBytes;
            maximumRecords = source.MaximumRecords;
        }

        var count = 0;
        DebianTrackerReader.Read(content, maximumBytes, maximumRecords, _ => { token.ThrowIfCancellationRequested(); count++; });
        var digest = SHA256.HashData(content);
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = clock.GetUtcNow().ToUniversalTime();
        var revision = new IntelligenceRevision
        {
            SourceId = sourceId, UpstreamVersion = Convert.ToHexStringLower(digest), RetrievedAt = now, Digest = digest,
            Artifact = content, SchemaVersion = SchemaVersion, MappingVersion = MappingVersion,
            LicenseEvidence = licenseEvidence, RecordCount = count, Normalized = false
        };
        Guid? restoreId = null;
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
        {
            var current = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
                .SingleOrDefaultAsync(token);
            if (current is null || !current.Enabled || current.Key != SourceKey) return new(DebianStageStatus.Unavailable);
            if (!current.Version.AsSpan().SequenceEqual(expectedSourceVersion)) return new(DebianStageStatus.Conflict);
            if (!await lease.VerifyHeldAsync(token)) return new(DebianStageStatus.Conflict);
            if (await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == digest && x.Revoked, token))
                return new(DebianStageStatus.Unavailable);
            // The source lease excludes another live importer. Keep revoked and referenced revisions as audit evidence.
            var abandoned = db.IntelligenceRevisions.Where(x => x.SourceId == sourceId && x.MappingVersion == MappingVersion && !x.Normalized && !x.Revoked &&
                !db.IntelligenceActivations.Any(a => a.RevisionId == x.Id) && !db.IntelligenceCaches.Any(c => c.RevisionId == x.Id) &&
                !db.IntelligenceActivationEvents.Any(e => e.RevisionId == x.Id || e.PreviousRevisionId == x.Id) &&
                !db.OfflineImports.Any(o => o.RevisionId == x.Id) &&
                !db.AdvisoryImports.Any(i => i.BaseRevisionId == x.Id || i.ResultRevisionId == x.Id));
            await db.DebianAdvisories.Where(x => abandoned.Select(r => r.Id).Contains(x.RevisionId)).ExecuteDeleteAsync(token);
            await abandoned.ExecuteDeleteAsync(token);
            var existing = await db.IntelligenceRevisions.Where(x => x.SourceId == sourceId && x.Digest == digest && x.MappingVersion == MappingVersion)
                .Select(x => new { x.Id, x.Normalized, x.ContentPurgedAt }).SingleOrDefaultAsync(token);
            if (existing is { Normalized: true })
            {
                await transaction.CommitAsync(token);
                return new(DebianStageStatus.Unchanged, existing.Id);
            }
            if (existing is { ContentPurgedAt: not null })
            {
                if (await db.DebianAdvisories.AnyAsync(x => x.RevisionId == existing.Id, token)) return new(DebianStageStatus.Conflict);
                restoreId = existing.Id;
                revision.Digest = RandomNumberGenerator.GetBytes(32);
            }
            else if (existing is not null) return new(DebianStageStatus.Unavailable);
            db.IntelligenceRevisions.Add(revision);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
        }
        db.ChangeTracker.Clear();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var channel = Channel.CreateBounded<DebianTrackerRecord>(new BoundedChannelOptions(BatchSize * 2)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        var producer = Task.Run(() =>
        {
            try
            {
                DebianTrackerReader.Read(content, maximumBytes, maximumRecords,
                    record => channel.Writer.WriteAsync(record, stop.Token).AsTask().GetAwaiter().GetResult());
                channel.Writer.TryComplete();
            }
            catch (Exception error) { channel.Writer.TryComplete(error); }
        }, CancellationToken.None);
        var batch = new List<DebianAdvisory>(BatchSize);
        var staged = 0;
        try
        {
            await foreach (var record in channel.Reader.ReadAllAsync(token))
            {
                batch.Add(new DebianAdvisory { RevisionId = revision.Id, SourcePackage = record.SourcePackage, AdvisoryId = record.AdvisoryId,
                    Release = record.Release, State = (int)record.State, FixedVersion = record.FixedVersion });
                if (batch.Count == BatchSize) await SaveBatchAsync();
            }
            if (batch.Count != 0) await SaveBatchAsync();
            await producer;
            if (staged != count) throw new InvalidDataException("Debian tracker record count changed during staging.");
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
            var current = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
                .SingleOrDefaultAsync(token);
            if (current is null || !current.Enabled || current.Key != SourceKey) return new(DebianStageStatus.Unavailable);
            if (!current.Version.AsSpan().SequenceEqual(expectedSourceVersion)) return new(DebianStageStatus.Conflict);
            if (!await lease.VerifyHeldAsync(token)) return new(DebianStageStatus.Conflict);
            var candidate = await db.IntelligenceRevisions.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceId] = {sourceId} AND [Id] = {revision.Id}")
                .Select(x => new { x.Id, x.Revoked, x.Normalized }).SingleOrDefaultAsync(token);
            if (candidate is null || candidate.Revoked || candidate.Normalized ||
                await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == digest && x.Revoked, token))
                return new(DebianStageStatus.Unavailable);
            if (await db.DebianAdvisories.CountAsync(x => x.RevisionId == revision.Id, token) != count)
                throw new InvalidDataException("Debian tracker staged index is incomplete.");
            if (restoreId is { } originalId)
            {
                var original = await db.IntelligenceRevisions.SingleOrDefaultAsync(x => x.Id == originalId && x.SourceId == sourceId && x.ContentPurgedAt != null && !x.Revoked, token);
                if (original is null || !original.Digest.AsSpan().SequenceEqual(digest) || await db.DebianAdvisories.AnyAsync(x => x.RevisionId == originalId, token))
                    return new(DebianStageStatus.Conflict);
                await db.DebianAdvisories.Where(x => x.RevisionId == candidate.Id).ExecuteUpdateAsync(x => x.SetProperty(a => a.RevisionId, originalId), token);
                original.Artifact = content; original.RecordCount = count; original.RetrievedAt = clock.GetUtcNow().ToUniversalTime();
                original.Normalized = true; original.ContentPurgedAt = null;
                await db.SaveChangesAsync(token);
                await db.IntelligenceRevisions.Where(x => x.Id == candidate.Id).ExecuteDeleteAsync(token);
                await transaction.CommitAsync(token);
                return new(DebianStageStatus.Staged, originalId);
            }
            await db.IntelligenceRevisions.Where(x => x.Id == candidate.Id).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.Normalized, true), token);
            await transaction.CommitAsync(token);
            return new(DebianStageStatus.Staged, revision.Id);
        }
        finally
        {
            await stop.CancelAsync();
            await producer;
        }

        async Task SaveBatchAsync()
        {
            if (staged % (BatchSize * 32) == 0 && !await lease.VerifyHeldAsync(token))
                throw new InvalidOperationException("Debian source refresh lease was lost.");
            db.DebianAdvisories.AddRange(batch);
            await db.SaveChangesAsync(token);
            staged += batch.Count;
            foreach (var entry in batch) db.Entry(entry).State = EntityState.Detached;
            batch.Clear();
        }
    }
}

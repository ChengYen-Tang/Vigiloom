using System.Security.Cryptography;
using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum MsrcCandidateStatus { Ready, Unavailable, Changed, TooLarge }
internal sealed record MsrcCandidate(string DocumentId, MsrcDocument Document, MsrcAdvisory Advisory);
internal sealed record MsrcCandidateResult(MsrcCandidateStatus Status, IReadOnlyList<MsrcCandidate> Items, Guid? SourceId = null,
    Guid? RevisionId = null, byte[]? SourceVersion = null, byte[]? ActivationVersion = null);

/// <summary>Finds candidate vendor documents for known CVEs; a missing candidate never establishes safety.</summary>
internal sealed class MsrcCandidateLookup(IDbContextFactory<SecurityAnalysisDbContext> contexts, TimeProvider clock)
{
    private const int MaximumCves = 200;
    private const int MaximumDocuments = 256;
    private const int MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumCandidates = 20_000;

    internal async Task<MsrcCandidateResult> FindAsync(Guid sourceId, Guid revisionId, IReadOnlyCollection<string> cves, CancellationToken token)
    {
        if (sourceId.Version != 7 || revisionId.Version != 7 || cves.Count > MaximumCves ||
            cves.Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 64 || x.Any(char.IsControl))) throw new ArgumentException("Invalid MSRC candidate request.");
        var requested = cves.ToHashSet(StringComparer.Ordinal);
        await using var db = await contexts.CreateDbContextAsync(token);
        var source = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId && x.Key == MsrcSnapshotStage.SourceKey, token);
        var activation = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        if (source is null || !source.Enabled || activation?.RevisionId != revisionId || source.LastObservedRevisionId != revisionId ||
            !Fresh(source.LastSuccessfulCheckAt, source.StaleAfterSeconds) ||
            !await MsrcRevisionEligibility.IsPublishableAsync(db, sourceId, revisionId, token)) return new(MsrcCandidateStatus.Unavailable, []);
        var ids = requested.ToArray();
        var documents = await (from index in db.MsrcCveIndexes.AsNoTracking()
            join document in db.MsrcDocuments.AsNoTracking() on index.Digest equals document.Digest
            where document.RevisionId == revisionId && ids.Contains(index.Cve)
            select new { document.DocumentId, document.Digest }).Distinct().OrderBy(x => x.DocumentId)
            .Take(MaximumDocuments + 1).ToArrayAsync(token);
        if (documents.Length > MaximumDocuments) return new(MsrcCandidateStatus.TooLarge, []);
        var matches = new List<MsrcCandidate>();
        var totalBytes = 0;
        foreach (var entry in documents)
        {
            token.ThrowIfCancellationRequested();
            var content = await db.MsrcArtifacts.AsNoTracking().Where(x => x.Digest == entry.Digest).Select(x => x.Content).SingleOrDefaultAsync(token);
            if (content is null) return new(MsrcCandidateStatus.Unavailable, []);
            if (content.Length > MaximumBytes - totalBytes) return new(MsrcCandidateStatus.TooLarge, []);
            totalBytes += content.Length;
            if (!SHA256.HashData(content).AsSpan().SequenceEqual(entry.Digest)) return new(MsrcCandidateStatus.Unavailable, []);
            MsrcDocument parsed;
            try { parsed = MsrcCvrfReader.Read(content, MaximumBytes, 100_000); }
            catch (InvalidDataException) { return new(MsrcCandidateStatus.Unavailable, []); }
            if (parsed.Id != entry.DocumentId) return new(MsrcCandidateStatus.Unavailable, []);
            foreach (var advisory in parsed.Advisories)
                if (requested.Contains(advisory.Cve))
                {
                    if (matches.Count == MaximumCandidates) return new(MsrcCandidateStatus.TooLarge, []);
                    matches.Add(new(entry.DocumentId, parsed, advisory));
                }
        }
        var latestSource = await db.IntelligenceSources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sourceId, token);
        var latestActivation = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        if (latestSource is null || !latestSource.Enabled || !latestSource.Version.AsSpan().SequenceEqual(source.Version) ||
            latestSource.LastObservedRevisionId != revisionId || !Fresh(latestSource.LastSuccessfulCheckAt, latestSource.StaleAfterSeconds) ||
            latestActivation is null || latestActivation.RevisionId != revisionId || !latestActivation.Version.AsSpan().SequenceEqual(activation.Version) ||
            !await MsrcRevisionEligibility.IsPublishableAsync(db, sourceId, revisionId, token)) return new(MsrcCandidateStatus.Changed, []);
        if (!Fresh(latestSource.LastSuccessfulCheckAt, latestSource.StaleAfterSeconds)) return new(MsrcCandidateStatus.Changed, []);
        return new(MsrcCandidateStatus.Ready, matches, sourceId, revisionId, source.Version, activation.Version);
    }

    internal async Task<bool> IsCurrentAsync(MsrcCandidateResult result, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var current = await IsCurrentAsync(db, result, token);
        await transaction.CommitAsync(token);
        return current;
    }

    // Caller owns the transaction; acquire the source lock before any Agent inventory lock.
    internal async Task<bool> IsCurrentAsync(SecurityAnalysisDbContext db, MsrcCandidateResult result, CancellationToken token)
    {
        if (result is not { Status: MsrcCandidateStatus.Ready, SourceId: { } sourceId, RevisionId: { } revisionId,
            SourceVersion: { Length: 8 } sourceVersion, ActivationVersion: { Length: 8 } activationVersion }) return false;
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .AsNoTracking().SingleOrDefaultAsync(token);
        var activation = await db.IntelligenceActivations.AsNoTracking().SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        var current = source is { Enabled: true, Key: MsrcSnapshotStage.SourceKey } && source.Version.AsSpan().SequenceEqual(sourceVersion) && source.LastObservedRevisionId == revisionId &&
            activation is not null && activation.RevisionId == revisionId && activation.Version.AsSpan().SequenceEqual(activationVersion) &&
            await MsrcRevisionEligibility.IsPublishableAsync(db, sourceId, revisionId, token) && Fresh(source.LastSuccessfulCheckAt, source.StaleAfterSeconds);
        return current;
    }

    private bool Fresh(DateTimeOffset? last, int seconds)
    {
        var now = clock.GetUtcNow();
        return last is { } value && seconds > 0 && value <= now && value >= now.AddSeconds(-seconds);
    }
}

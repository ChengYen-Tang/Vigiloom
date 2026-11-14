using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

public enum IntelligencePublicationResult { Published, Unchanged, NotFound, Conflict, Unavailable, ConfirmationRequired }

/// <summary>Atomically selects a retained validated revision and records the decision.</summary>
public sealed class IntelligencePublication(IDbContextFactory<SecurityAnalysisDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, TimeProvider clock)
{
    public Task<IntelligencePublicationResult> ActivateAsync(Guid sourceId, Guid revisionId, Guid? expectedActiveRevisionId,
        string expectedVersion, bool confirmLargeReduction, Guid actorId, CancellationToken token)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("An administrator is required.", nameof(actorId));
        return ActivateCoreAsync(sourceId, revisionId, expectedActiveRevisionId, expectedVersion, confirmLargeReduction, actorId, token);
    }

    internal Task<IntelligencePublicationResult> ActivateScheduledAsync(Guid sourceId, Guid revisionId, Guid? expectedActiveRevisionId,
        string expectedVersion, CancellationToken token) => ActivateCoreAsync(sourceId, revisionId, expectedActiveRevisionId, expectedVersion, false, null, token);

    private async Task<IntelligencePublicationResult> ActivateCoreAsync(Guid sourceId, Guid revisionId, Guid? expectedActiveRevisionId,
        string expectedVersion, bool confirmLargeReduction, Guid? actorId, CancellationToken token)
    {
        if (sourceId == Guid.Empty || revisionId == Guid.Empty || expectedActiveRevisionId == Guid.Empty)
            throw new ArgumentException("Invalid source or revision identity.");
        if (expectedVersion is null || expectedVersion.Length > 32) return IntelligencePublicationResult.Conflict;
        byte[] version;
        try { version = Convert.FromBase64String(expectedVersion); }
        catch (FormatException) { return IntelligencePublicationResult.Conflict; }
        if (version.Length is not (0 or 8)) return IntelligencePublicationResult.Conflict;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources
            .FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (source is null) return IntelligencePublicationResult.NotFound;
        var now = clock.GetUtcNow().ToUniversalTime();
        if (!source.Enabled || source.Key is not ("cisa-kev" or "github-reviewed") || source.LastSuccessfulCheckAt is null ||
            source.LastSuccessfulCheckAt > now || source.LastSuccessfulCheckAt < now.AddSeconds(-source.StaleAfterSeconds)) return IntelligencePublicationResult.Unavailable;
        var active = await db.IntelligenceActivations.SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        if (active?.RevisionId != expectedActiveRevisionId || (active is null ? version.Length != 0 : !active.Version.AsSpan().SequenceEqual(version)))
            return IntelligencePublicationResult.Conflict;
        var candidate = await db.IntelligenceRevisions
            .FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceId] = {sourceId} AND [Id] = {revisionId}")
            .Select(x => new { x.Id, x.RecordCount, x.Revoked, x.Normalized, x.SchemaVersion, x.MappingVersion, x.LicenseEvidence, x.Digest }).SingleOrDefaultAsync(token);
        if (candidate is null) return IntelligencePublicationResult.NotFound;
        if (candidate.Revoked || !candidate.Normalized) return IntelligencePublicationResult.Unavailable;
        if (source.Key == "cisa-kev" && (candidate.SchemaVersion != KevCatalogValidator.SchemaRevision ||
            candidate.MappingVersion != KevUpdate.MappingVersion || candidate.LicenseEvidence != KevUpdate.LicenseEvidence))
            return IntelligencePublicationResult.Unavailable;
        if (source.Key == "github-reviewed" && !await AdvisoryRevisionEligibility.IsPublishableAsync(db, sourceId, revisionId, token))
            return IntelligencePublicationResult.Unavailable;
        if (await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == candidate.Digest && x.Revoked, token))
            return IntelligencePublicationResult.Unavailable;
        if (active?.RevisionId == candidate.Id) return IntelligencePublicationResult.Unchanged;
        int? previousCount = null;
        if (active is not null)
            previousCount = await db.IntelligenceRevisions.Where(x => x.SourceId == sourceId && x.Id == active.RevisionId)
                .Select(x => (int?)x.RecordCount).SingleAsync(token);
        var largeReduction = candidate.RecordCount == 0 || previousCount is > 0 && (long)candidate.RecordCount * 10 < (long)previousCount * 9;
        if (largeReduction && !confirmLargeReduction) return IntelligencePublicationResult.ConfirmationRequired;
        var previous = active?.RevisionId;
        if (active is null)
        {
            active = new IntelligenceActivation { SourceId = sourceId };
            db.IntelligenceActivations.Add(active);
        }
        active.RevisionId = candidate.Id;
        active.ActivatedAt = now;
        db.IntelligenceActivationEvents.Add(new IntelligenceActivationEvent
        {
            SourceId = sourceId, PreviousRevisionId = previous, RevisionId = candidate.Id, OccurredAt = now,
            ActorId = actorId, Scheduled = actorId is null, LargeReductionConfirmed = largeReduction
        });
        await db.SaveChangesAsync(token);
        if (actorId is { } actor)
            await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
                new OperationAudit(actor, sourceId, now, new IntelligenceRevisionActivated(previous, candidate.Id, largeReduction)), token);
        await transaction.CommitAsync(token);
        return IntelligencePublicationResult.Published;
    }
}

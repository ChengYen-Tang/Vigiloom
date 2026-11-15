using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.SignatureBase;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

public enum IntelligencePublicationResult { Published, Unchanged, NotFound, Conflict, Unavailable, ConfirmationRequired, OfflineConfirmationRequired }

/// <summary>Atomically selects a retained validated revision and records the decision.</summary>
public sealed class IntelligencePublication(IDbContextFactory<SecurityAnalysisDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, TimeProvider clock)
{
    public Task<IntelligencePublicationResult> ActivateAsync(Guid sourceId, Guid revisionId, Guid? expectedActiveRevisionId,
        string expectedVersion, bool confirmLargeReduction, Guid actorId, CancellationToken token)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("An administrator is required.", nameof(actorId));
        return ActivateCoreAsync(sourceId, revisionId, expectedActiveRevisionId, expectedVersion, confirmLargeReduction, false, actorId, token);
    }

    public Task<IntelligencePublicationResult> ActivateAsync(Guid sourceId, Guid revisionId, Guid? expectedActiveRevisionId,
        string expectedVersion, bool confirmLargeReduction, bool confirmOfflineSource, Guid actorId, CancellationToken token)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("An administrator is required.", nameof(actorId));
        return ActivateCoreAsync(sourceId, revisionId, expectedActiveRevisionId, expectedVersion, confirmLargeReduction, confirmOfflineSource, actorId, token);
    }

    internal Task<IntelligencePublicationResult> ActivateScheduledAsync(Guid sourceId, Guid revisionId, Guid? expectedActiveRevisionId,
        string expectedVersion, CancellationToken token) => ActivateCoreAsync(sourceId, revisionId, expectedActiveRevisionId, expectedVersion, false, false, null, token);

    internal Task<IntelligencePublicationResult> ActivateLatestScheduledAsync(Guid sourceId, Guid revisionId, Guid? expectedActiveRevisionId,
        string expectedVersion, CancellationToken token) => ActivateCoreAsync(sourceId, revisionId, expectedActiveRevisionId, expectedVersion, false, false, null, token, true);

    private async Task<IntelligencePublicationResult> ActivateCoreAsync(Guid sourceId, Guid revisionId, Guid? expectedActiveRevisionId,
        string expectedVersion, bool confirmLargeReduction, bool confirmOfflineSource, Guid? actorId, CancellationToken token, bool requireLatest = false)
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
        if (!source.Enabled || source.Key is not ("cisa-kev" or "github-reviewed" or "sigmahq-rules" or "signature-base" or "ubuntu-osv" or "debian-security" or "msrc-cvrf" or "first-epss") && BaselineDefinitions.Find(source.Key) is null) return IntelligencePublicationResult.Unavailable;
        if (requireLatest && source.LastObservedRevisionId != revisionId)
            return IntelligencePublicationResult.Conflict;
        var active = await db.IntelligenceActivations.SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        if (active?.RevisionId != expectedActiveRevisionId || (active is null ? version.Length != 0 : !active.Version.AsSpan().SequenceEqual(version)))
            return IntelligencePublicationResult.Conflict;
        var candidate = await db.IntelligenceRevisions
            .FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceId] = {sourceId} AND [Id] = {revisionId}")
            .Select(x => new { x.Id, x.RecordCount, x.Revoked, x.Normalized, x.SchemaVersion, x.MappingVersion, x.LicenseEvidence, x.Digest, x.ReleasedAt }).SingleOrDefaultAsync(token);
        if (candidate is null) return IntelligencePublicationResult.NotFound;
        if (candidate.Revoked || !candidate.Normalized) return IntelligencePublicationResult.Unavailable;
        var offline = source.Key == "cisa-kev" && candidate.MappingVersion == KevUpdate.OfflineMappingVersion;
        if (source.Key == "cisa-kev")
        {
            if (candidate.SchemaVersion != KevCatalogValidator.SchemaRevision ||
                (offline ? candidate.LicenseEvidence != KevUpdate.OfflineLicenseEvidence :
                    candidate.MappingVersion != KevUpdate.MappingVersion || candidate.LicenseEvidence != KevUpdate.LicenseEvidence) ||
                !await KevFreshness.IsFreshAsync(db, source, candidate.Id, candidate.MappingVersion, candidate.ReleasedAt, now, token))
                return IntelligencePublicationResult.Unavailable;
            if (offline && (!confirmOfflineSource || actorId is null)) return IntelligencePublicationResult.OfflineConfirmationRequired;
        }
        else if (source.LastSuccessfulCheckAt is null || source.LastSuccessfulCheckAt > now ||
                 source.LastSuccessfulCheckAt < now.AddSeconds(-source.StaleAfterSeconds)) return IntelligencePublicationResult.Unavailable;
        if (source.Key == "github-reviewed" && !await AdvisoryRevisionEligibility.IsPublishableAsync(db, sourceId, revisionId, token))
            return IntelligencePublicationResult.Unavailable;
        if (BaselineDefinitions.Find(source.Key) is not null && !await BaselineEligibility.IsEligibleAsync(db, source.Key, sourceId, revisionId, token))
            return IntelligencePublicationResult.Unavailable;
        if (source.Key == UbuntuRevisionStage.SourceKey && !await UbuntuRevisionEligibility.IsPublishableAsync(db, sourceId, revisionId, now, token))
            return IntelligencePublicationResult.Unavailable;
        if (source.Key == DebianRevisionStage.SourceKey && !await DebianRevisionEligibility.IsEligibleAsync(db, sourceId, revisionId, token))
            return IntelligencePublicationResult.Unavailable;
        if (source.Key == EpssSnapshotStage.SourceKey && !await EpssRevisionEligibility.IsPublishableAsync(db, sourceId, revisionId, now, source.StaleAfterSeconds, token))
            return IntelligencePublicationResult.Unavailable;
        int? msrcCount = null;
        if (source.Key == MsrcSnapshotStage.SourceKey && (msrcCount = await MsrcRevisionEligibility.AssociationCountAsync(db, sourceId, revisionId, token)) is null)
            return IntelligencePublicationResult.Unavailable;
        int? sigmaCount = null;
        if (source.Key == SigmaRefresh.SourceKey && (sigmaCount = await SigmaRevisionEligibility.IncludedCountAsync(db, sourceId, revisionId, token)) is null)
            return IntelligencePublicationResult.Unavailable;
        IocAssessment? iocAssessment = null;
        if (source.Key == "signature-base" && (iocAssessment = await IocSourceEligibility.AssessAsync(db, sourceId, revisionId, token)) is null)
            return IntelligencePublicationResult.Unavailable;
        if (await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Digest == candidate.Digest && x.Revoked, token))
            return IntelligencePublicationResult.Unavailable;
        if (active?.RevisionId == candidate.Id)
        {
            if (VendorRefreshStatus.HasPublicationError(source.LastError))
            {
                source.LastError = null;
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            return IntelligencePublicationResult.Unchanged;
        }
        int? previousCount = null;
        var previousMsrcUnknown = false;
        var previousIocUnknown = false;
        var iocCategoryReduction = false;
        if (active is not null && msrcCount is not null)
        {
            previousCount = await MsrcRevisionEligibility.AssociationCountAsync(db, sourceId, active.RevisionId, token);
            previousMsrcUnknown = previousCount is null;
        }
        else if (active is not null && sigmaCount is not null)
            previousCount = await db.SigmaManifests.Where(x => x.RevisionId == active.RevisionId).Select(x => (int?)x.IncludedCount).SingleOrDefaultAsync(token);
        else if (active is not null && iocAssessment is not null)
        {
            var previousIoc = await IocSourceEligibility.AssessAsync(db, sourceId, active.RevisionId, token);
            previousCount = previousIoc?.ValidCount;
            previousIocUnknown = previousIoc is null;
            if (previousIoc is not null)
                iocCategoryReduction = previousIoc.HashCount > 0 && (long)iocAssessment.HashCount * 10 < (long)previousIoc.HashCount * 9 ||
                    previousIoc.C2Count > 0 && (long)iocAssessment.C2Count * 10 < (long)previousIoc.C2Count * 9;
        }
        else if (active is not null)
            previousCount = await db.IntelligenceRevisions.Where(x => x.SourceId == sourceId && x.Id == active.RevisionId)
                .Select(x => (int?)x.RecordCount).SingleAsync(token);
        var candidateCount = msrcCount ?? sigmaCount ?? iocAssessment?.ValidCount ?? candidate.RecordCount;
        var largeReduction = previousMsrcUnknown || previousIocUnknown || iocCategoryReduction || candidateCount == 0 || previousCount is > 0 && (long)candidateCount * 10 < (long)previousCount * 9;
        if (largeReduction && !confirmLargeReduction) return IntelligencePublicationResult.ConfirmationRequired;
        var previous = active?.RevisionId;
        if (active is null)
        {
            active = new IntelligenceActivation { SourceId = sourceId };
            db.IntelligenceActivations.Add(active);
        }
        active.RevisionId = candidate.Id;
        active.ActivatedAt = now;
        if (VendorRefreshStatus.HasPublicationError(source.LastError)) source.LastError = null;
        db.IntelligenceActivationEvents.Add(new IntelligenceActivationEvent
        {
            SourceId = sourceId, PreviousRevisionId = previous, RevisionId = candidate.Id, OccurredAt = now,
            ActorId = actorId, Scheduled = actorId is null, LargeReductionConfirmed = largeReduction
        });
        await db.SaveChangesAsync(token);
        if (actorId is { } actor)
            await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
                new OperationAudit(actor, sourceId, now, new IntelligenceRevisionActivated(previous, candidate.Id, largeReduction, offline)), token);
        await transaction.CommitAsync(token);
        return IntelligencePublicationResult.Published;
    }
}

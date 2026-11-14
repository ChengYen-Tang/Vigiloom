using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

public enum IntelligenceRevocationResult { Revoked, AlreadyRevoked, NotFound }

/// <summary>Withdraws a retained revision and invalidates active consumers of the same content.</summary>
public sealed class IntelligenceRevocation(IDbContextFactory<SecurityAnalysisDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, TimeProvider clock)
{
    public async Task<IntelligenceRevocationResult> RevokeAsync(Guid sourceId, Guid revisionId, Guid actorId, CancellationToken token)
    {
        if (sourceId == Guid.Empty || revisionId.Version != 7 || actorId == Guid.Empty) throw new ArgumentException("Invalid identity.");
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (source is null) return IntelligenceRevocationResult.NotFound;
        var revision = await db.IntelligenceRevisions.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceRevisions] WITH (UPDLOCK, HOLDLOCK) WHERE [SourceId] = {sourceId} AND [Id] = {revisionId}")
            .SingleOrDefaultAsync(token);
        if (revision is null) return IntelligenceRevocationResult.NotFound;
        if (revision.Revoked) return IntelligenceRevocationResult.AlreadyRevoked;
        var active = await db.IntelligenceActivations.SingleOrDefaultAsync(x => x.SourceId == sourceId, token);
        // Advisory snapshots can retain entries from earlier revisions. Invalidate the
        // active token whenever any advisory provenance is revoked; readers recheck eligibility.
        if (active is not null && (source.Key == "github-reviewed" ||
            await db.IntelligenceRevisions.AnyAsync(x => x.SourceId == sourceId && x.Id == active.RevisionId && x.Digest == revision.Digest, token)))
            db.Entry(active).Property(x => x.ActivatedAt).IsModified = true;
        revision.Revoked = true;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actorId, sourceId, clock.GetUtcNow(), new IntelligenceRevisionRevoked(revisionId)), token);
        await transaction.CommitAsync(token);
        return IntelligenceRevocationResult.Revoked;
    }
}

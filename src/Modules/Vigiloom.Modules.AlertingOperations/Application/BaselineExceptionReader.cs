using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application;

/// <summary>Reads recent administrator explanations without changing baseline outcomes.</summary>
public sealed class BaselineExceptionReader(IDbContextFactory<OperationsDbContext> contexts) : IBaselineExceptionReader
{
    public async Task<IReadOnlyList<BaselineExceptionNote>> GetRecentAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var rows = await db.AuditEvents.AsNoTracking().Where(x => x.ResourceId == deviceId && x.Kind == AuditKinds.BaselineExceptionRecordedV1)
            .OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.RowId).Take(100)
            .Select(x => new { x.ActorId, x.OccurredAt, x.Details }).ToArrayAsync(cancellationToken);
        return rows.Select(x => (x.ActorId, x.OccurredAt, Note: OperationAuditQueryService.ReadBaselineException(x.Details)))
            .Where(x => x.Note is not null)
            .Select(x => new BaselineExceptionNote(x.Note!.SourceId, x.Note.RevisionId, x.Note.CheckId, x.Note.Reason, x.ActorId, x.OccurredAt)).ToArray();
    }
}

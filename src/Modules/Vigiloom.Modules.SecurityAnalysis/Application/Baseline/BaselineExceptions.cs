using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Baseline;

public enum BaselineExceptionStatus { Recorded, Invalid, NotFound, VersionChanged }

/// <summary>Records an explanation against an exact policy revision without changing its result.</summary>
public sealed class BaselineExceptions(IDbContextFactory<SecurityAnalysisDbContext> contexts, InventoryConfiguration inventory,
    IBaselineSource sources, IOperationAuditWriter<OperationAudit> audit, TimeProvider clock)
{
    public async Task<BaselineExceptionStatus> RecordAsync(Guid deviceId, string sourceKey, Guid revisionId, string? reason, Guid actorId, CancellationToken token)
    {
        var text = reason?.Trim();
        if (deviceId.Version != 7 || revisionId.Version != 7 || actorId == Guid.Empty || string.IsNullOrWhiteSpace(sourceKey) || sourceKey.Length > 128 ||
            string.IsNullOrWhiteSpace(text) || text.Length > 200 || text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            return BaselineExceptionStatus.Invalid;
        if (await inventory.GetAsync(deviceId, token) is null) return BaselineExceptionStatus.NotFound;
        var selected = await sources.GetActiveAsync(sourceKey, token);
        if (selected is not { Availability: BaselineSourceAvailability.Ready, SourceId: not null, RevisionId: not null, Check: not null } ||
            selected.RevisionId != revisionId) return BaselineExceptionStatus.VersionChanged;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actorId, deviceId, clock.GetUtcNow().ToUniversalTime(),
                new BaselineExceptionRecorded(selected.SourceId.Value, revisionId, selected.Check.Id, text)), token);
        await transaction.CommitAsync(token);
        return BaselineExceptionStatus.Recorded;
    }
}

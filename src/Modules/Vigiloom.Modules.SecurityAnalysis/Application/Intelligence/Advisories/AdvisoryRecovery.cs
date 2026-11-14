using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Advisories;

public enum AdvisoryRecoveryResult { Abandoned, NotFound, Conflict }

/// <summary>Allows an administrator to abandon a failed import while preserving its evidence and rate limit.</summary>
public sealed class AdvisoryRecovery(IDbContextFactory<SecurityAnalysisDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, TimeProvider clock)
{
    public async Task<AdvisoryRecoveryResult> AbandonAsync(Guid sourceId, AdvisoryImportRecoveryRequest request, Guid actorId, CancellationToken token)
    {
        if (sourceId == Guid.Empty || actorId == Guid.Empty || request.ImportId.Version != 7 || request.Version is null || request.Version.Length > 32)
            return AdvisoryRecoveryResult.Conflict;
        byte[] version;
        try { version = Convert.FromBase64String(request.Version); }
        catch (FormatException) { return AdvisoryRecoveryResult.Conflict; }
        if (version.Length != 8) return AdvisoryRecoveryResult.Conflict;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources
            .FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (source is null || source.Key != "github-reviewed") return AdvisoryRecoveryResult.NotFound;
        var import = await db.AdvisoryImports
            .FromSqlInterpolated($"SELECT * FROM [security].[AdvisoryImports] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {request.ImportId} AND [SourceId] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (import is null) return AdvisoryRecoveryResult.NotFound;
        if (import.Phase is not (1 or 2 or 3) || !import.Version.AsSpan().SequenceEqual(version) ||
            (import.FailureCode is null && (import.Phase != 3 || import.ReconciliationPhase != 4))) return AdvisoryRecoveryResult.Conflict;
        import.Phase = 5;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actorId, sourceId, clock.GetUtcNow().ToUniversalTime(), new AdvisoryImportAbandoned(import.Id)), token);
        await transaction.CommitAsync(token);
        return AdvisoryRecoveryResult.Abandoned;
    }
}

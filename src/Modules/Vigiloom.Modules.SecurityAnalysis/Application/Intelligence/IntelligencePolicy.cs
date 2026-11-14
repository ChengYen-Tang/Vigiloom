using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

public enum IntelligencePolicyResult { Updated, Unchanged, NotFound, Conflict, Invalid }

/// <summary>Applies source settings with a version check and transactional audit.</summary>
public sealed class IntelligencePolicy(IDbContextFactory<SecurityAnalysisDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit, TimeProvider clock)
{
    public static bool Valid(IntelligencePolicyUpdate? value) => value is not null &&
        IntelligencePolicyLimits.Valid(value.UpdateIntervalSeconds, value.StaleAfterSeconds, value.MaximumBytes, value.MaximumRecords);

    public async Task<IntelligencePolicyResult> UpdateAsync(Guid sourceId, IntelligencePolicyUpdate input, Guid actor, CancellationToken token)
    {
        if (sourceId == Guid.Empty || actor == Guid.Empty || !Valid(input)) throw new ArgumentException("Invalid intelligence source policy.");
        if (input.Version is null || input.Version.Length > 32) return IntelligencePolicyResult.Conflict;
        byte[] version;
        try { version = Convert.FromBase64String(input.Version); }
        catch (FormatException) { return IntelligencePolicyResult.Conflict; }
        if (version.Length != 8) return IntelligencePolicyResult.Conflict;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources
            .FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .SingleOrDefaultAsync(token);
        if (source is null) return IntelligencePolicyResult.NotFound;
        if (!source.Version.AsSpan().SequenceEqual(version)) return IntelligencePolicyResult.Conflict;
        if (!IntelligencePolicyLimits.ValidForSource(source.Key, input.MaximumBytes, input.MaximumRecords)) return IntelligencePolicyResult.Invalid;
        var before = Values(source);
        var after = new IntelligencePolicyValues(input.Enabled, input.UpdateIntervalSeconds, input.StaleAfterSeconds, input.MaximumBytes, input.MaximumRecords);
        if (before == after) return IntelligencePolicyResult.Unchanged;
        source.Enabled = input.Enabled;
        source.UpdateIntervalSeconds = input.UpdateIntervalSeconds;
        source.StaleAfterSeconds = input.StaleAfterSeconds;
        source.MaximumBytes = input.MaximumBytes;
        source.MaximumRecords = input.MaximumRecords;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actor, source.Id, clock.GetUtcNow().ToUniversalTime(),
                new IntelligencePolicyChanged(before, after, Convert.ToBase64String(version), Convert.ToBase64String(source.Version))), token);
        await transaction.CommitAsync(token);
        return IntelligencePolicyResult.Updated;
    }

    private static IntelligencePolicyValues Values(IntelligenceSource value) => new(value.Enabled, value.UpdateIntervalSeconds,
        value.StaleAfterSeconds, value.MaximumBytes, value.MaximumRecords);
}

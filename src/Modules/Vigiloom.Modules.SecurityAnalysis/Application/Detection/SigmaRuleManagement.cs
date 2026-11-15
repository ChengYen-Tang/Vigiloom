using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Sigma;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Detection;

internal enum SigmaRuleChangeResult { Changed, Unchanged, Conflict, TestUnavailable, SourceUnavailable }

/// <summary>Applies a tested rule selection and its audit record in the same transaction.</summary>
internal sealed class SigmaRuleManagement(IDbContextFactory<SecurityAnalysisDbContext> contexts, SigmaCatalog catalog,
    IOperationAuditWriter<OperationAudit> audit, TimeProvider clock)
{
    internal async Task<SigmaRuleChangeResult> SetAsync(Guid sourceId, string path, Guid? testId, bool enabled,
        string expectedVersion, Guid actorId, CancellationToken token)
    {
        if (sourceId.Version != 7 || actorId.Version != 7 || string.IsNullOrWhiteSpace(path) || path.Length > 512 ||
            enabled && testId is not { Version: 7 }) throw new ArgumentException("Invalid rule change.");
        byte[] version;
        try { version = Convert.FromBase64String(expectedVersion); }
        catch (FormatException) { return SigmaRuleChangeResult.Conflict; }
        if (version.Length is not (0 or 8)) return SigmaRuleChangeResult.Conflict;

        SigmaSnapshot? snapshot = null;
        SigmaSourceContent? content = null;
        SigmaCorrelationEntry? correlation = null;
        if (enabled)
        {
            snapshot = await catalog.GetActiveAsync(token);
            if (snapshot.Availability != SigmaAvailability.Ready || snapshot.SourceId != sourceId)
                return SigmaRuleChangeResult.SourceUnavailable;
            content = await catalog.ReadRuleAsync(snapshot, path, token);
            if (content.Availability != SigmaAvailability.Ready || content.Rule is null || content.Content is null)
                return SigmaRuleChangeResult.SourceUnavailable;
            var metadata = SigmaRuleValidator.Validate(content.Content);
            if (metadata.IsCorrelation)
            {
                var rules = await new SigmaRuleSetLoader(catalog).LoadAsync(token);
                if (rules.Snapshot != snapshot) return SigmaRuleChangeResult.SourceUnavailable;
                correlation = rules.Correlations.SingleOrDefault(x => x.Source.Path == path && x.Source.Sha256 == content.Rule.Sha256);
                if (correlation is null) return SigmaRuleChangeResult.SourceUnavailable;
            }
            else if (SigmaRuleCompiler.Compile(content.Content, metadata).Rule is null) return SigmaRuleChangeResult.SourceUnavailable;
        }

        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var source = await db.IntelligenceSources.FromSqlInterpolated($"SELECT * FROM [security].[IntelligenceSources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}")
            .AsNoTracking().SingleOrDefaultAsync(token);
        if (source is null) return SigmaRuleChangeResult.SourceUnavailable;
        var current = await db.SigmaRuleActivations.SingleOrDefaultAsync(x => x.SourceId == sourceId && x.Path == path, token);
        if (current is null ? version.Length != 0 : !current.Version.AsSpan().SequenceEqual(version)) return SigmaRuleChangeResult.Conflict;

        if (enabled)
        {
            if (await catalog.CheckSelectionAsync(db, snapshot!, token) != SigmaAvailability.Ready) return SigmaRuleChangeResult.SourceUnavailable;
            var test = await db.SigmaRuleTests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == testId && x.SourceId == sourceId, token);
            if (test is null || !test.Passed || test.RevisionId != snapshot!.RevisionId || test.ActivationVersion != snapshot.ActivationVersion ||
                test.MatcherVersion != SigmaTesting.MatcherVersion || !string.Equals(test.Path, path, StringComparison.Ordinal) ||
                !string.Equals(test.Digest, content!.Rule!.Sha256, StringComparison.Ordinal) ||
                !SigmaExceptions.TryRead(test.SamplesJson, out _) ||
                correlation is not null && !CorrelationTestEvidence.Matches(test.SamplesJson, correlation)) return SigmaRuleChangeResult.TestUnavailable;
            if (current is { Enabled: true } && current.TestId == test.Id) return SigmaRuleChangeResult.Unchanged;
            if (current is null)
            {
                current = new SigmaRuleActivation { Id = Guid.CreateVersion7(), SourceId = sourceId, Path = path };
                db.SigmaRuleActivations.Add(current);
            }
            current.TestId = test.Id;
            current.Enabled = true;
        }
        else
        {
            if (current is null || !current.Enabled) return SigmaRuleChangeResult.Unchanged;
            current.Enabled = false;
        }
        var now = clock.GetUtcNow().ToUniversalTime();
        current.UpdatedBy = actorId;
        current.UpdatedAt = now;
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
            new OperationAudit(actorId, current.Id, now, new SigmaRuleStateChanged(current.TestId, current.Enabled)), token);
        await transaction.CommitAsync(token);
        return SigmaRuleChangeResult.Changed;
    }
}

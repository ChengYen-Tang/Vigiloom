using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Baseline;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Baseline;

/// <summary>Stores baseline results with the Agent receiver's receipt transaction.</summary>
public sealed class BaselineWriter : IBaselineWriter
{
    public async Task<bool> WriteAsync(DbConnection connection, DbTransaction transaction, Guid agentId, Guid deviceId,
        long deviceGeneration, BaselineBatch batch, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        var sourceIds = batch.Items.Select(x => x.SourceId).Distinct().ToArray();
        var revisionIds = batch.Items.Select(x => x.RevisionId).Distinct().ToArray();
        var sources = await db.IntelligenceSources.AsNoTracking().Where(x => sourceIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var published = await db.IntelligenceActivationEvents.AsNoTracking()
            .Where(x => sourceIds.Contains(x.SourceId) && revisionIds.Contains(x.RevisionId))
            .Select(x => new { x.SourceId, x.RevisionId }).Distinct().ToArrayAsync(cancellationToken);
        var publishedPairs = published.Select(x => (x.SourceId, x.RevisionId)).ToHashSet();
        foreach (var item in batch.Items)
        {
            if (!sources.TryGetValue(item.SourceId, out var source) ||
                BaselineDefinitions.Find(source.Key) is not { } definition || definition.Check.Id != item.CheckId ||
                !publishedPairs.Contains((item.SourceId, item.RevisionId))) return false;
        }
        foreach (var item in batch.Items)
            db.Set<BaselineObservation>().Add(new BaselineObservation
            {
                AgentId = agentId, DeviceId = deviceId, DeviceGeneration = deviceGeneration, BatchId = batch.BatchId,
                SourceId = item.SourceId, RevisionId = item.RevisionId, ActivationVersion = item.ActivationVersion,
                CheckId = item.CheckId, ObservedAt = item.ObservedAt, ReceivedAt = receivedAt, OperatingSystem = item.OperatingSystem,
                Outcome = item.Outcome, Evidence = item.Evidence
            });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

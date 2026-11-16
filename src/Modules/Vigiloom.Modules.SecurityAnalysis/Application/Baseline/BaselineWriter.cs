using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Baseline;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Baseline;

/// <summary>Stores baseline results with the Agent receiver's receipt transaction.</summary>
public sealed class BaselineWriter : IBaselineWriter
{
    public async Task WriteAsync(DbConnection connection, DbTransaction transaction, Guid agentId, Guid deviceId,
        long deviceGeneration, BaselineBatch batch, DateTimeOffset receivedAt, CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        foreach (var item in batch.Items)
            db.Set<BaselineObservation>().Add(new BaselineObservation
            {
                AgentId = agentId, DeviceId = deviceId, DeviceGeneration = deviceGeneration, BatchId = batch.BatchId,
                SourceId = item.SourceId, RevisionId = item.RevisionId, ActivationVersion = item.ActivationVersion,
                CheckId = item.CheckId, ObservedAt = item.ObservedAt, ReceivedAt = receivedAt, OperatingSystem = item.OperatingSystem,
                Outcome = item.Outcome, Evidence = item.Evidence
            });
        await db.SaveChangesAsync(cancellationToken);
    }
}

using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

public sealed class InventoryStatusReader : IInventoryStatusReader
{
    public async Task<IReadOnlyList<InventorySourceStatus>> ReadAsync(DbConnection connection, DbTransaction transaction,
        InventoryOwner owner, CancellationToken cancellationToken)
    {
        if (!await InventoryAgentLock.TryAcquireAsync(connection, transaction, owner.AgentId, 5000, cancellationToken))
            throw new TimeoutException("Inventory status is temporarily busy.");
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        var sources = await db.InventorySources.AsNoTracking()
            .Where(x => x.AgentId == owner.AgentId && x.DeviceId == owner.DeviceId && x.DeviceGeneration == owner.DeviceGeneration)
            .OrderBy(x => x.Source).ToArrayAsync(cancellationToken);
        var generations = sources.Where(x => x.LastAttemptGeneration is not null).Select(x => x.LastAttemptGeneration!.Value).ToArray();
        var snapshots = await db.InventorySnapshots.AsNoTracking()
            .Where(x => x.AgentId == owner.AgentId && generations.Contains(x.Generation))
            .Select(x => new { x.Source, x.Generation, x.SnapshotId }).ToArrayAsync(cancellationToken);
        var lookup = snapshots.ToDictionary(x => (x.Source, x.Generation), x => x.SnapshotId);
        return sources.Select(x => new InventorySourceStatus(x.Source, Math.Max(x.ClosedGeneration, x.ActiveGeneration ?? 0),
            x.ActiveGeneration, x.LastAttemptGeneration, x.LastOutcome, (InventoryCompletion?)x.LastStatus, x.LastRejectionReason,
            x.LastAttemptGeneration is { } generation && lookup.TryGetValue((x.Source, generation), out var snapshotId) ? snapshotId : null)).ToArray();
    }
}

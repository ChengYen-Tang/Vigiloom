using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

/// <summary>Stages and publishes inventory under the receiver's transaction and Agent lock.</summary>
public sealed class InventoryStageWriter : IInventoryStageWriter
{
    private readonly InventoryPublicationGate gate;

    public InventoryStageWriter() : this(InventoryPublicationGate.Shared) { }
    internal InventoryStageWriter(InventoryPublicationGate gate) => this.gate = gate;

    public async Task<InventoryStageResult> StageAsync(DbConnection connection, DbTransaction transaction, InventoryOwner owner,
        InventoryChunk chunk, DateTimeOffset databaseNow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.AgentId.Version != 7 || owner.DeviceId.Version != 7 || owner.DeviceGeneration < 0)
            throw new ArgumentException("A registered Agent and device generation are required.", nameof(owner));
        if (databaseNow == default || databaseNow.Offset != TimeSpan.Zero)
            throw new ArgumentException("The database timestamp must be UTC.", nameof(databaseNow));
        if (!await InventoryAgentLock.TryAcquireAsync(connection, transaction, owner.AgentId, 5000, cancellationToken))
            return InventoryStageResult.CapacityExceeded;

        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, cancellationToken);
        var result = await InventoryStaging.StoreAsync(db, owner, chunk, databaseNow, cancellationToken);
        if (result.Disposition == InventoryStaging.Disposition.Ready)
        {
            // The final chunk can materialize two full snapshots; avoid multiplying that
            // memory cost by the much larger general Agent request concurrency limit.
            if (!gate.TryEnter()) return InventoryStageResult.CapacityExceeded;
            try { await InventoryPublication.FinishAsync(db, result.State!, result.Snapshot!, databaseNow, cancellationToken); }
            finally { gate.Exit(); }
        }
        return result.Disposition switch
        {
            InventoryStaging.Disposition.Stored or InventoryStaging.Disposition.Ready => InventoryStageResult.Accepted,
            InventoryStaging.Disposition.Conflict => InventoryStageResult.Conflict,
            InventoryStaging.Disposition.Expired => InventoryStageResult.Expired,
            InventoryStaging.Disposition.Busy => InventoryStageResult.CapacityExceeded,
            InventoryStaging.Disposition.TooLarge => InventoryStageResult.SnapshotTooLarge,
            _ => throw new InvalidOperationException("Unknown inventory staging disposition.")
        };
    }
}

using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class InventoryStagingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Store_AcceptsOutOfOrderAndIdenticalRetriesButRejectsConflicts() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var now = DateTimeOffset.UtcNow;
            InventoryEntry[][] items = [[new("first", "first", "1", "x64")], [new("second", "second", "1", "x64")]];
            var measurements = items.Select(InventoryDigest.MeasureChunk).ToArray();
            var digest = InventoryDigest.Combine(measurements.Select(x => x.Digest).ToArray());
            var bytes = measurements.Sum(x => (long)x.Bytes);
            var snapshotId = Guid.CreateVersion7();
            var chunks = Enumerable.Range(0, 2).Select(index => new InventoryChunk(4, Guid.CreateVersion7(), now,
                snapshotId, 1, InventorySource.LinuxRpm, now, InventoryOutcome.Complete,
                index, 2, 2, bytes, digest, items[index])).ToArray();

            var first = await InventoryStaging.StoreAsync(db, owner, chunks[1], now, token);
            Assert.AreEqual(InventoryStaging.Disposition.Stored, first.Disposition);
            Assert.AreEqual(InventoryStaging.Disposition.Stored, (await InventoryStaging.StoreAsync(db, owner, chunks[1], now, token)).Disposition);
            Assert.AreEqual(InventoryStaging.Disposition.Conflict, (await InventoryStaging.StoreAsync(db, owner,
                chunks[1] with { BatchId = Guid.CreateVersion7() }, now, token)).Disposition);
            Assert.AreEqual(InventoryStaging.Disposition.Conflict, (await InventoryStaging.StoreAsync(db, owner,
                chunks[0] with { Digest = new byte[32] }, now, token)).Disposition);
            var last = await InventoryStaging.StoreAsync(db, owner, chunks[0], now, token);
            Assert.AreEqual(InventoryStaging.Disposition.Ready, last.Disposition);
            Assert.AreEqual(2, last.Snapshot!.ReceivedChunks);
            Assert.AreEqual(2, await db.InventoryChunks.CountAsync(token));
            var newer = chunks[0] with { Generation = 2, SnapshotId = Guid.CreateVersion7(), BatchId = Guid.CreateVersion7() };
            var superseding = await InventoryStaging.StoreAsync(db, owner, newer, now, token);
            Assert.AreEqual(InventoryStaging.Disposition.Stored, superseding.Disposition);
            Assert.AreEqual(1L, superseding.State!.ClosedGeneration);
            Assert.AreEqual(InventoryStaging.Disposition.Expired,
                (await InventoryStaging.StoreAsync(db, owner, chunks[1], now, token)).Disposition);
            Assert.AreEqual(1, await db.InventoryChunks.CountAsync(token));
            await transaction.RollbackAsync(token);
            db.ChangeTracker.Clear();
            Assert.AreEqual(0, await db.InventorySnapshots.CountAsync(token));
        }, TestContext.CancellationToken);
}

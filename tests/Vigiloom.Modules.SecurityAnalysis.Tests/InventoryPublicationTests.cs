using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class InventoryPublicationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Finish_OnlyPublishesVerifiedCompleteSnapshotsAndPersistsChanges() =>
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

            async Task Publish(InventoryChunk chunk)
            {
                var staged = await InventoryStaging.StoreAsync(db, owner, chunk, now, token);
                Assert.AreEqual(InventoryStaging.Disposition.Ready, staged.Disposition);
                await InventoryPublication.FinishAsync(db, staged.State!, staged.Snapshot!, now, token);
            }

            await Publish(Chunk(1, InventoryOutcome.Complete, [new("one", "one", "1", "x64"), new("removed", "removed", "1", "x64")]));
            await Publish(Chunk(2, InventoryOutcome.Partial, []));
            var state = await db.InventorySources.SingleAsync(token);
            Assert.AreEqual(1L, state.CurrentGeneration);
            Assert.AreEqual(InventorySnapshotStatus.Partial, state.LastStatus);
            Assert.AreEqual(0, await db.InventoryChanges.CountAsync(token));

            await Publish(Chunk(3, InventoryOutcome.Complete, [new("one", "one", "2", "x64"), new("added", "added", "1", "x64")]));
            Assert.AreEqual(3L, state.CurrentGeneration);
            var changes = await db.InventoryChanges.AsNoTracking().ToListAsync(token);
            Assert.HasCount(3, changes);
            Assert.IsTrue(changes.All(x => x.RecordedAt == now));
            Assert.IsTrue(changes.All(x => x.ObservedAt == now.AddDays(-30)));
            Assert.IsTrue(changes.Any(x => x.Key == "one" && x.Kind == (int)InventoryChangeKind.Modified));
            Assert.IsTrue(changes.Any(x => x.Key == "added" && x.Kind == (int)InventoryChangeKind.Added));
            Assert.IsTrue(changes.Any(x => x.Key == "removed" && x.Kind == (int)InventoryChangeKind.Removed));
            Assert.IsFalse(await db.InventoryChunks.AnyAsync(x => x.Generation == 1, token));

            var invalid = Chunk(4, InventoryOutcome.Complete, []) with { Digest = new byte[32] };
            await Publish(invalid);
            Assert.AreEqual(3L, state.CurrentGeneration);
            Assert.AreEqual(InventorySnapshotStatus.Rejected, state.LastStatus);
            Assert.AreEqual(InventoryRejectionReason.Integrity, state.LastRejectionReason);
            Assert.HasCount(3, await db.InventoryChanges.ToListAsync(token));
            await transaction.RollbackAsync(token);

            InventoryChunk Chunk(long generation, InventoryOutcome outcome, InventoryEntry[] items)
            {
                var measured = InventoryDigest.MeasureChunk(items);
                return new(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), generation,
                    InventorySource.LinuxRpm, now.AddDays(-30), outcome, 0, 1, items.Length, measured.Bytes,
                    InventoryDigest.Combine([measured.Digest]), items);
            }
        }, TestContext.CancellationToken);
}

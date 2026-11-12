using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
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
    public Task Finish_ProcessPartialRemainsQueryableWithoutReplacingCompleteBaseline() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var previous = db.Database.GetMigrations().Single(x => x.EndsWith("_PartialObservationChanges", StringComparison.Ordinal));
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(previous, token);
            await migrator.MigrateAsync(null, token);
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var now = DateTimeOffset.UtcNow;
            var start = now.AddHours(-1);
            var item = new InventoryEntry("linux:boot:42:1", "worker", NumericId: 42, ProcessStartedAt: start);

            async Task Publish(long generation, InventoryOutcome outcome, InventoryEntry[] items)
            {
                var measured = InventoryDigest.MeasureChunk(items);
                var chunk = new InventoryChunk(5, Guid.CreateVersion7(), now, Guid.CreateVersion7(), generation,
                    InventorySource.HostProcesses, now, outcome, 0, 1, items.Length, measured.Bytes, InventoryDigest.Combine([measured.Digest]), items,
                    outcome == InventoryOutcome.Partial ? InventoryPartialReason.AccessDenied : null);
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var staged = await InventoryStaging.StoreAsync(db, owner, chunk, now, token);
                Assert.AreEqual(InventoryStaging.Disposition.Ready, staged.Disposition);
                await InventoryPublication.FinishAsync(db, staged.State!, staged.Snapshot!, now, token);
                await transaction.CommitAsync(token);
                db.ChangeTracker.Clear();
            }

            await Publish(1, InventoryOutcome.Complete, [item]);
            await Publish(2, InventoryOutcome.Partial, [item]);
            await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(previous, token));
            var query = new InventoryQuery(new TestFactory(options));
            var view = await query.GetAsync(owner.DeviceId, owner.AgentId, token);
            var source = view.Sources.Single();
            Assert.IsNotNull(source.CurrentSnapshotId);
            Assert.IsNotNull(source.PartialSnapshotId);
            Assert.AreEqual(InventoryPartialReason.AccessDenied, source.PartialReason);
            Assert.AreNotEqual(source.CurrentSnapshotId, source.PartialSnapshotId);
            Assert.IsNotNull(await query.GetChunkAsync(owner.DeviceId, owner.AgentId, InventorySource.HostProcesses, source.PartialSnapshotId.Value, 0, token));
            Assert.AreEqual(0, await db.InventoryChanges.CountAsync(token));

            await Publish(3, InventoryOutcome.Complete, []);
            Assert.IsNull(await query.GetChunkAsync(owner.DeviceId, owner.AgentId, InventorySource.HostProcesses, source.PartialSnapshotId.Value, 0, token));
            Assert.AreEqual(1, await db.InventoryChanges.CountAsync(token));
            Assert.IsFalse(await db.InventoryChunks.AnyAsync(x => x.Generation == 2, token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task Stage_ProcessPartialIsClearedOnCapacityRejectionAndExpiry() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var now = DateTimeOffset.UtcNow;
            var item = new InventoryEntry("linux:boot:42:1", "worker", NumericId: 42, ProcessStartedAt: now);

            InventoryChunk Chunk(long generation, InventoryOutcome outcome, InventoryEntry[] items, int index = 0, int count = 1)
            {
                var measured = InventoryDigest.MeasureChunk(items);
                return new(5, Guid.CreateVersion7(), now, Guid.CreateVersion7(), generation, InventorySource.HostProcesses,
                    now, outcome, index, count, items.Length, measured.Bytes, InventoryDigest.Combine([measured.Digest]), items);
            }
            async Task Publish(InventoryChunk chunk)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var staged = await InventoryStaging.StoreAsync(db, owner, chunk, now, token);
                Assert.AreEqual(InventoryStaging.Disposition.Ready, staged.Disposition);
                await InventoryPublication.FinishAsync(db, staged.State!, staged.Snapshot!, now, token);
                await transaction.CommitAsync(token);
                db.ChangeTracker.Clear();
            }

            await Publish(Chunk(1, InventoryOutcome.Complete, [item]));
            await Publish(Chunk(2, InventoryOutcome.Partial, [item]));
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                var oversized = Chunk(3, InventoryOutcome.Partial, [item]) with { TotalBytes = InventoryChunkCodec.MaximumSnapshotBytes };
                var rejected = await InventoryStaging.StoreAsync(db, owner, oversized, now, token);
                Assert.AreEqual(InventoryStaging.Disposition.TooLarge, rejected.Disposition);
                await transaction.CommitAsync(token);
                db.ChangeTracker.Clear();
            }
            Assert.IsFalse(await db.InventoryChunks.AnyAsync(x => x.Generation == 2, token));
            Assert.IsTrue(await db.InventoryChunks.AnyAsync(x => x.Generation == 1, token));
            await Publish(Chunk(4, InventoryOutcome.Partial, [item]));
            await using (var transaction = await db.Database.BeginTransactionAsync(token))
            {
                // Stage the first of two chunks, then simulate the expiry worker closing it.
                var first = Chunk(5, InventoryOutcome.Partial, [item], 0, 2) with { TotalItems = 2 };
                var staged = await InventoryStaging.StoreAsync(db, owner, first, now, token);
                Assert.AreEqual(InventoryStaging.Disposition.Stored, staged.Disposition);
                await InventoryStaging.CloseExpiredAsync(db, staged.State!, staged.Snapshot!, now, token);
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                db.ChangeTracker.Clear();
            }
            Assert.IsFalse(await db.InventoryChunks.AnyAsync(x => x.Generation == 4 || x.Generation == 5, token));
            Assert.IsTrue(await db.InventoryChunks.AnyAsync(x => x.Generation == 1, token));
        }, TestContext.CancellationToken);

    private sealed class TestFactory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }

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

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class InventoryStageWriterTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Stage_BusyPublicationRollsBackLastChunkAndRetryPublishes() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            InventoryEntry[] first = [new("one", "one", "1", "x64")];
            InventoryEntry[] second = [new("two", "two", "1", "x64")];
            var a = InventoryDigest.MeasureChunk(first);
            var b = InventoryDigest.MeasureChunk(second);
            var chunk = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 2, 2, a.Bytes + b.Bytes,
                InventoryDigest.Combine([a.Digest, b.Digest]), first);
            var last = chunk with { BatchId = Guid.CreateVersion7(), ChunkIndex = 1, Items = second };
            var gate = new InventoryPublicationGate(1);
            var writer = new InventoryStageWriter(gate);
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(InventoryStageResult.Accepted, await writer.StageAsync(connection, tx, owner, chunk, now, token));
                await tx.CommitAsync(token);
            }
            Assert.IsTrue(gate.TryEnter());
            try
            {
                await using var tx = await connection.BeginTransactionAsync(token);
                Assert.AreEqual(InventoryStageResult.CapacityExceeded, await writer.StageAsync(connection, tx, owner, last, now, token));
                await tx.RollbackAsync(token);
            }
            finally { gate.Exit(); }
            Assert.AreEqual(1, await db.InventoryChunks.CountAsync(token));
            Assert.IsNull((await db.InventorySources.AsNoTracking().SingleAsync(token)).CurrentGeneration);
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(InventoryStageResult.Accepted, await writer.StageAsync(connection, tx, owner, last, now, token));
                await tx.CommitAsync(token);
            }
            Assert.AreEqual(2, await db.InventoryChunks.CountAsync(token));
            Assert.AreEqual(1L, (await db.InventorySources.AsNoTracking().SingleAsync(token)).CurrentGeneration);
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task Stage_HostHardwareSourcesAreAcceptedByMigratedSchema() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 0);
            var now = DateTimeOffset.UtcNow;
            var measured = InventoryDigest.MeasureChunk([]);
            foreach (var source in new[] { InventorySource.HostNetwork, InventorySource.HostVolumes })
            {
                var chunk = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1, source,
                    now, InventoryOutcome.Complete, 0, 1, 0, measured.Bytes, InventoryDigest.Combine([measured.Digest]), []);
                await using var transaction = await connection.BeginTransactionAsync(token);
                Assert.AreEqual(InventoryStageResult.Accepted, await new InventoryStageWriter().StageAsync(connection, transaction, owner, chunk, now, token));
                await transaction.CommitAsync(token);
            }
            Assert.AreEqual(2, await db.InventorySources.CountAsync(token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task Stage_UsesCallerTransactionAndDoesNotCloseItsConnection() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);
            var now = DateTimeOffset.UtcNow;
            var measured = InventoryDigest.MeasureChunk([]);
            var chunk = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 1, 0, measured.Bytes,
                InventoryDigest.Combine([measured.Digest]), []);
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);

            var writer = new InventoryStageWriter();
            Assert.AreEqual(InventoryStageResult.Accepted, await writer.StageAsync(connection, transaction, owner, chunk, now, token));
            Assert.AreEqual(System.Data.ConnectionState.Open, connection.State);
            await transaction.RollbackAsync(token);
            Assert.AreEqual(0, await db.InventorySources.CountAsync(token));
            Assert.AreEqual(0, await db.InventorySnapshots.CountAsync(token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task Stage_FailedSupersessionCanRollBackToCommittedStaging() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            var now = DateTimeOffset.UtcNow;
            InventoryEntry[] items = [new("a", "a", "1", "x64")];
            var measured = InventoryDigest.MeasureChunk(items);
            var old = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 2, 2, measured.Bytes * 2L,
                InventoryDigest.Combine([measured.Digest, measured.Digest]), items);
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var writer = new InventoryStageWriter();
            await using (var initial = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(InventoryStageResult.Accepted, await writer.StageAsync(connection, initial, owner, old, now, token));
                await initial.CommitAsync(token);
            }
            await using (var replacement = await connection.BeginTransactionAsync(token))
            {
                var malformed = old with { Generation = 2, SnapshotId = Guid.CreateVersion7(), BatchId = Guid.CreateVersion7(), TotalBytes = 4 };
                Assert.AreEqual(InventoryStageResult.Conflict, await writer.StageAsync(connection, replacement, owner, malformed, now, token));
                await replacement.RollbackAsync(token);
            }
            var source = await db.InventorySources.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(1L, source.ActiveGeneration);
            Assert.AreEqual(0L, source.ClosedGeneration);
            Assert.AreEqual(1, await db.InventoryChunks.CountAsync(token));
            Assert.AreEqual(1, await db.InventorySnapshots.CountAsync(token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task Stage_PublicationFailureRollsBackDeletedOldChunksAndWrittenChanges() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var writer = new InventoryStageWriter();
            InventoryChunk Chunk(long generation, string version)
            {
                InventoryEntry[] entries = [new("package", "package", version, "x64")];
                var measured = InventoryDigest.MeasureChunk(entries);
                return new(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), generation,
                    InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 1, 1,
                    measured.Bytes, InventoryDigest.Combine([measured.Digest]), entries);
            }
            await using (var initial = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(InventoryStageResult.Accepted, await writer.StageAsync(connection, initial, owner, Chunk(1, "1"), now, token));
                await initial.CommitAsync(token);
            }
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER [security].[TestFailChangeInsert] ON [security].[InventoryChanges]
                AFTER INSERT AS THROW 51042, 'Injected publication failure', 1;
                """, token);
            await using (var replacement = await connection.BeginTransactionAsync(token))
            {
                await Assert.ThrowsAsync<DbUpdateException>(() => writer.StageAsync(connection, replacement, owner, Chunk(2, "2"), now, token));
                await replacement.RollbackAsync(token);
            }
            var source = await db.InventorySources.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(1L, source.CurrentGeneration);
            Assert.AreEqual(1L, source.ClosedGeneration);
            Assert.IsNull(source.ActiveGeneration);
            Assert.AreEqual(1, await db.InventoryChunks.CountAsync(token));
            Assert.AreEqual(1, await db.InventorySnapshots.CountAsync(token));
            Assert.AreEqual(0, await db.InventoryChanges.CountAsync(token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task Stage_LaterChangeBatchFailureRollsBackEarlierBatch() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var writer = new InventoryStageWriter();
            var baselineMeasure = InventoryDigest.MeasureChunk([]);
            var baseline = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 1, 0, baselineMeasure.Bytes,
                InventoryDigest.Combine([baselineMeasure.Digest]), []);
            await using (var initial = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(InventoryStageResult.Accepted, await writer.StageAsync(connection, initial, owner, baseline, now, token));
                await initial.CommitAsync(token);
            }
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [security].[InventoryChanges] ADD CONSTRAINT [TestRejectLast] CHECK ([Key] <> 'p128')", token);
            InventoryEntry[][] items =
            [
                Enumerable.Range(0, 100).Select(i => new InventoryEntry($"p{i:000}", $"p{i:000}", "1", "x64")).ToArray(),
                Enumerable.Range(100, 29).Select(i => new InventoryEntry($"p{i:000}", $"p{i:000}", "1", "x64")).ToArray()
            ];
            var measurements = items.Select(InventoryDigest.MeasureChunk).ToArray();
            var digest = InventoryDigest.Combine(measurements.Select(x => x.Digest).ToArray());
            var snapshotId = Guid.CreateVersion7();
            var chunks = Enumerable.Range(0, 2).Select(i => new InventoryChunk(4, Guid.CreateVersion7(), now, snapshotId, 2,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, i, 2, 129,
                measurements.Sum(x => (long)x.Bytes), digest, items[i])).ToArray();
            await using (var first = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(InventoryStageResult.Accepted, await writer.StageAsync(connection, first, owner, chunks[0], now, token));
                await first.CommitAsync(token);
            }
            await using (var last = await connection.BeginTransactionAsync(token))
            {
                await Assert.ThrowsAsync<DbUpdateException>(() => writer.StageAsync(connection, last, owner, chunks[1], now, token));
                await last.RollbackAsync(token);
            }
            var source = await db.InventorySources.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(1L, source.CurrentGeneration);
            Assert.AreEqual(2L, source.ActiveGeneration);
            Assert.AreEqual(0, await db.InventoryChanges.CountAsync(token));
            Assert.AreEqual(2, await db.InventoryChunks.CountAsync(token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task Stage_OverQuotaRecordsTerminalFenceWithoutReceipt() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 0);
            var source = new InventorySourceState(owner, InventorySource.HostSystem) { ClosedGeneration = 1, CurrentGeneration = 1 };
            var reserved = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
                InventorySource.HostSystem, now, InventoryOutcome.Complete, 0, 1, 0,
                InventoryChunkCodec.MaximumSnapshotBytes - 1, new byte[32], []);
            var current = new InventorySnapshot(owner, reserved, now)
            {
                Status = InventorySnapshotStatus.Complete, FinishedAt = now, ReceivedChunks = 1,
                ReceivedItems = 0, ReceivedBytes = reserved.TotalBytes
            };
            db.InventorySources.Add(source);
            db.InventorySnapshots.Add(current);
            await db.SaveChangesAsync(token);
            var measured = InventoryDigest.MeasureChunk([]);
            var over = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 5,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 1, 0, measured.Bytes,
                InventoryDigest.Combine([measured.Digest]), []);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            InventoryEntry[] oldItems = [new("old", "old", "1", "x64")];
            var oldMeasure = InventoryDigest.MeasureChunk(oldItems);
            var pending = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 3,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Partial, 0, 2, 2, oldMeasure.Bytes * 2L,
                InventoryDigest.Combine([oldMeasure.Digest, oldMeasure.Digest]), oldItems);
            await using (var pendingTransaction = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(InventoryStageResult.Accepted, await new InventoryStageWriter().StageAsync(connection, pendingTransaction, owner, pending, now, token));
                await pendingTransaction.CommitAsync(token);
            }
            await using (var tx = await connection.BeginTransactionAsync(token))
            {
                var result = await new InventoryStageWriter().StageAsync(connection, tx, owner, over, now, token);
                Assert.AreEqual(InventoryStageResult.SnapshotTooLarge, result);
                await tx.CommitAsync(token);
            }
            db.ChangeTracker.Clear();
            var rejectedSource = await db.InventorySources.AsNoTracking().SingleAsync(x => x.Source == InventorySource.LinuxDpkg, token);
            Assert.AreEqual(5L, rejectedSource.ClosedGeneration);
            Assert.AreEqual(InventorySnapshotStatus.Rejected, rejectedSource.LastStatus);
            Assert.AreEqual(InventoryRejectionReason.Capacity, rejectedSource.LastRejectionReason);
            Assert.IsNull(rejectedSource.ActiveGeneration);
            Assert.IsNull(rejectedSource.CurrentGeneration);
            Assert.AreEqual(InventorySnapshotStatus.Rejected,
                (await db.InventorySnapshots.AsNoTracking().SingleAsync(x => x.Source == InventorySource.LinuxDpkg && x.Generation == 5, token)).Status);
            Assert.AreEqual(InventorySnapshotStatus.Expired,
                (await db.InventorySnapshots.AsNoTracking().SingleAsync(x => x.Source == InventorySource.LinuxDpkg && x.Generation == 3, token)).Status);
            Assert.AreEqual(0, await db.InventoryChunks.CountAsync(x => x.Source == InventorySource.LinuxDpkg, token));
            await using (var retry = await connection.BeginTransactionAsync(token))
            {
                Assert.AreEqual(InventoryStageResult.Expired, await new InventoryStageWriter().StageAsync(connection, retry, owner, over, now, token));
                var statuses = await new InventoryStatusReader().ReadAsync(connection, retry, owner, token);
                var status = statuses.Single(x => x.Source == InventorySource.LinuxDpkg);
                Assert.AreEqual(5L, status.HighWaterGeneration);
                Assert.AreEqual(InventoryCompletion.Rejected, status.LastStatus);
                Assert.AreEqual(InventoryRejectionReason.Capacity, status.LastRejectionReason);
                Assert.AreEqual(over.SnapshotId, status.LastSnapshotId);
                await retry.RollbackAsync(token);
            }
        }, TestContext.CancellationToken);
}

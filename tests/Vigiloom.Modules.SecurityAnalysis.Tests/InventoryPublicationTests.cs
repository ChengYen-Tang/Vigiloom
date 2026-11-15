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
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public Task WindowsRelationships_DowngradeProtectsSnapshotsHistoryAndWindowsUpdateSources(int kind) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrations = db.Database.GetMigrations().ToArray();
            var fence = Array.FindIndex(migrations, x => x.EndsWith("_VulnerabilityAssessment", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, fence);
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var item = new InventoryEntry($"{id:D}:1", "update", UpdateId: id, UpdateRevision: 1, UpdateInstalled: true,
                UpdateRebootRequired: false, UpdateCves: [], UpdateKbs: [], UpdateServerSelection: 0, UpdateSupersededIds: []);
            var measured = InventoryDigest.MeasureChunk([item]);
            var chunk = new InventoryChunk(InventoryChunkCodec.WindowsUpdateRelationshipsSchemaVersion, Guid.CreateVersion7(), now,
                Guid.CreateVersion7(), 1, InventorySource.WindowsUpdate, now, InventoryOutcome.Partial, 0, 1, 1,
                measured.Bytes, InventoryDigest.Combine([measured.Digest]), [item]);
            var snapshot = new InventorySnapshot(owner, chunk, now);
            if (kind == 0) db.InventorySnapshots.Add(snapshot);
            if (kind is 1 or 2)
            {
                // Even an empty relationship array is new-schema evidence that older readers cannot understand.
                const string json = "{\"UpdateSupersededIds\":[]}";
                db.InventoryChanges.Add(new InventoryChangeRecord(snapshot, now, kind == 1 ? 3 : 1, item.Key, kind == 1 ? json : null, kind == 2 ? json : null));
            }
            if (kind is 0 or 3) db.InventorySources.Add(new InventorySourceState(owner, InventorySource.WindowsUpdate));
            await db.SaveChangesAsync(token);
            var migrator = db.GetService<IMigrator>();
            await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[fence - 1], token));
        }, TestContext.CancellationToken);

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public Task WindowsUpdate_MigrationBlocksDowngradeForRetainedEvidence(int kind) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrations = db.Database.GetMigrations().ToArray();
            var fence = Array.FindIndex(migrations, x => x.EndsWith("_VulnerabilityAssessment", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, fence);
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var item = kind == 3 ? new InventoryEntry("host", "machine", "10", "X64", OperatingSystem: "Windows", ProcessArchitecture: "X64",
                Runtime: ".NET", AvailableProcessorCount: 2, OperatingSystemFamily: "Windows", WindowsEdition: "ServerStandard",
                WindowsBuild: 20348, WindowsRevision: 1, WindowsInstallationType: "Server Core") :
                new InventoryEntry($"{id:D}:1", "update", UpdateId: id, UpdateRevision: 1,
                    UpdateInstalled: true, UpdateRebootRequired: false, UpdateCves: ["CVE-2025-1234"], UpdateKbs: []);
            var measured = InventoryDigest.MeasureChunk([item]);
            var chunk = new InventoryChunk(InventoryChunkCodec.WindowsUpdateSchemaVersion, Guid.CreateVersion7(), now,
                Guid.CreateVersion7(), 1, kind == 3 ? InventorySource.HostSystem : InventorySource.WindowsUpdate,
                now, kind == 3 ? InventoryOutcome.Complete : InventoryOutcome.Partial, 0, 1, 1,
                measured.Bytes, InventoryDigest.Combine([measured.Digest]), [item]);
            var snapshot = new InventorySnapshot(owner, chunk, now);
            if (kind is 0 or 1) db.InventorySources.Add(new InventorySourceState(owner, InventorySource.WindowsUpdate));
            if (kind == 1) db.InventorySnapshots.Add(snapshot);
            if (kind is 2 or 3) db.InventoryChanges.Add(new InventoryChangeRecord(snapshot, now, 1, item.Key, null, System.Text.Json.JsonSerializer.Serialize(item)));
            await db.SaveChangesAsync(token);
            var migrator = db.GetService<IMigrator>();
            await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[fence - 1], token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task WindowsUpdate_PartialSnapshotPersistsWithoutCompleteCoverage() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var id = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var item = new InventoryEntry($"{id:D}:1", "update", UpdateId: id, UpdateRevision: 1,
                UpdateInstalled: true, UpdateRebootRequired: false, UpdateCves: ["CVE-2025-1234"], UpdateKbs: []);
            var measured = InventoryDigest.MeasureChunk([item]);
            var chunk = new InventoryChunk(InventoryChunkCodec.WindowsUpdateSchemaVersion, Guid.CreateVersion7(), now,
                Guid.CreateVersion7(), 1, InventorySource.WindowsUpdate, now, InventoryOutcome.Partial, 0, 1, 1,
                measured.Bytes, InventoryDigest.Combine([measured.Digest]), [item]);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var staged = await InventoryStaging.StoreAsync(db, owner, chunk, now, token);
            Assert.AreEqual(InventoryStaging.Disposition.Ready, staged.Disposition);
            await InventoryPublication.FinishAsync(db, staged.State!, staged.Snapshot!, now, token);
            await transaction.CommitAsync(token);
            db.ChangeTracker.Clear();
            var state = await db.InventorySources.AsNoTracking().SingleAsync(x => x.AgentId == owner.AgentId, token);
            Assert.IsNull(state.CurrentGeneration);
            Assert.AreEqual(InventorySnapshotStatus.Partial, state.LastStatus);
            Assert.AreEqual(1, await db.InventoryChunks.CountAsync(x => x.AgentId == owner.AgentId, token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task WindowsRelease_PublishesAndQueriesVersionChanges() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var item = new InventoryEntry("host", "machine", "10.0.20348", "X64", OperatingSystem: "Windows", ProcessArchitecture: "X64",
                Runtime: ".NET", AvailableProcessorCount: 2, OperatingSystemFamily: "Windows", WindowsEdition: "ServerStandard", WindowsBuild: 20348, WindowsRevision: 3000);
            var first = await Publish(1, item);
            var second = await Publish(2, item with { WindowsRevision = 3001 });
            var query = new InventoryQuery(new TestFactory(options));
            var view = await query.GetAsync(owner.DeviceId, owner.AgentId, token);
            Assert.AreEqual(second, view.Sources.Single().CurrentSnapshotId);
            var payload = await query.GetChunkAsync(owner.DeviceId, owner.AgentId, InventorySource.HostSystem, second, 0, token);
            Assert.IsNotNull(payload);
            Assert.AreEqual(3001, payload.Items.Single().WindowsRevision);
            var change = await db.InventoryChanges.AsNoTracking().SingleAsync(token);
            Assert.Contains("3000", change.BeforeJson!);
            Assert.Contains("3001", change.AfterJson!);
            Assert.AreNotEqual(first, second);

            async Task<Guid> Publish(long generation, InventoryEntry entry)
            {
                var measured = InventoryDigest.MeasureChunk([entry]);
                var chunk = new InventoryChunk(InventoryChunkCodec.WindowsReleaseSchemaVersion, Guid.CreateVersion7(), now,
                    Guid.CreateVersion7(), generation, InventorySource.HostSystem, now, InventoryOutcome.Complete, 0, 1, 1,
                    measured.Bytes, InventoryDigest.Combine([measured.Digest]), [entry]);
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                var staged = await InventoryStaging.StoreAsync(db, owner, chunk, now, token);
                Assert.AreEqual(InventoryStaging.Disposition.Ready, staged.Disposition);
                await InventoryPublication.FinishAsync(db, staged.State!, staged.Snapshot!, now, token);
                await transaction.CommitAsync(token);
                db.ChangeTracker.Clear();
                return chunk.SnapshotId;
            }
        }, TestContext.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Migration_WindowsReleasePreventsDataLossOnDowngrade(bool historyOnly) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrations = db.Database.GetMigrations().ToArray();
            var fence = Array.FindIndex(migrations, x => x.EndsWith("_VulnerabilityAssessment", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, fence);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[fence - 1], token);
            await migrator.MigrateAsync(null, token);
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var item = new InventoryEntry("host", "machine", "10.0.20348", "X64", OperatingSystem: "Windows", ProcessArchitecture: "X64",
                Runtime: ".NET", AvailableProcessorCount: 2, OperatingSystemFamily: "Windows", WindowsEdition: "ServerStandard", WindowsBuild: 20348, WindowsRevision: 3000);
            var measured = InventoryDigest.MeasureChunk([item]);
            var chunk = new InventoryChunk(InventoryChunkCodec.WindowsReleaseSchemaVersion, Guid.CreateVersion7(), now,
                Guid.CreateVersion7(), 1, InventorySource.HostSystem, now, InventoryOutcome.Complete, 0, 1, 1,
                measured.Bytes, InventoryDigest.Combine([measured.Digest]), [item]);
            var snapshot = new InventorySnapshot(owner, chunk, now);
            if (historyOnly)
                db.InventoryChanges.Add(new InventoryChangeRecord(snapshot, now, 1, item.Key, null, System.Text.Json.JsonSerializer.Serialize(item)));
            else
            {
                db.InventorySources.Add(new InventorySourceState(owner, chunk.Source));
                db.InventorySnapshots.Add(snapshot);
            }
            await db.SaveChangesAsync(token);
            await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[fence - 1], token));
        }, TestContext.CancellationToken);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Migration_DpkgSourceSchemaPreventsDataLossOnDowngrade(bool historyOnly) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrations = db.Database.GetMigrations().ToArray();
            var fence = Array.FindIndex(migrations, x => x.EndsWith("_VulnerabilityAssessment", StringComparison.Ordinal));
            Assert.IsGreaterThan(0, fence);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[fence - 1], token);
            await migrator.MigrateAsync(null, token);
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var item = new InventoryEntry("foo", "foo", "1.0-1", "amd64", SourcePackageName: "foo-source", SourcePackageVersion: "1.0-1");
            var measured = InventoryDigest.MeasureChunk([item]);
            var chunk = new InventoryChunk(InventoryChunkCodec.DpkgSourceSchemaVersion, Guid.CreateVersion7(), now,
                Guid.CreateVersion7(), 1, InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 1, 1,
                measured.Bytes, InventoryDigest.Combine([measured.Digest]), [item]);
            var snapshot = new InventorySnapshot(owner, chunk, now);
            if (historyOnly)
                db.InventoryChanges.Add(new InventoryChangeRecord(snapshot, now, 1, item.Key, null, System.Text.Json.JsonSerializer.Serialize(item)));
            else
            {
                db.InventorySources.Add(new InventorySourceState(owner, chunk.Source));
                db.InventorySnapshots.Add(snapshot);
            }
            await db.SaveChangesAsync(token);
            await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync(migrations[fence - 1], token));
        }, TestContext.CancellationToken);

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
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

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
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

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

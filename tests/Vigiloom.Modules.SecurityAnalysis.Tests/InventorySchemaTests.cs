using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class InventorySchemaTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Migration_EnforcesManifestLinksAndCompleteCounts() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var chunk = new InventoryChunk(InventoryChunkCodec.SchemaVersion, Guid.CreateVersion7(), now,
                Guid.CreateVersion7(), 1, InventorySource.LinuxDpkg, now, InventoryOutcome.Complete,
                0, 1, 0, 2, new byte[32], []);
            await using (var db = new SecurityAnalysisDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                var error = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO [security].[InventoryChunks] ([AgentId], [Source], [Generation], [ChunkIndex], [Payload])
                    VALUES ({owner.AgentId}, {(int)chunk.Source}, {chunk.Generation}, {chunk.ChunkIndex}, {new byte[] { 1 }})
                    """, token));
                Assert.AreEqual(547, error.Number);
                db.InventorySources.Add(new InventorySourceState(owner, chunk.Source));
                db.InventorySnapshots.Add(new InventorySnapshot(owner, chunk, now));
                await db.SaveChangesAsync(token);
                var invalidReason = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [security].[InventorySources] SET [LastRejectionReason] = 1
                    WHERE [AgentId] = {owner.AgentId} AND [Source] = {(int)chunk.Source}
                    """, token));
                Assert.AreEqual(547, invalidReason.Number);
                Assert.Contains("CK_InventorySources_RejectionReason", invalidReason.Message);
                var incomplete = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE [security].[InventorySnapshots] SET [Status] = 2, [FinishedAt] = {now}
                    WHERE [AgentId] = {owner.AgentId} AND [Generation] = 1
                    """, token));
                Assert.AreEqual(547, incomplete.Number);
                db.InventoryChunks.Add(new InventoryChunkData(owner.AgentId, chunk, InventoryChunkCodec.Serialize(chunk)));
                await db.SaveChangesAsync(token);
                Assert.AreEqual(1, await db.InventoryChunks.CountAsync(token));
            }
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task Migration_RefusesDowngradeWithAnExistingFence() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("0", token);
            await db.Database.MigrateAsync(token);
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            db.InventorySources.Add(new InventorySourceState(owner, InventorySource.LinuxDpkg) { ClosedGeneration = 42 });
            await db.SaveChangesAsync(token);
            var failure = await Assert.ThrowsAsync<SqlException>(() => migrator.MigrateAsync("0", token));
            Assert.AreEqual(51000, failure.Number);
            Assert.AreEqual(42L, await db.Database.SqlQuery<long>($"SELECT [ClosedGeneration] AS [Value] FROM [security].[InventorySources]").SingleAsync(token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task Migration_RefusesToDiscardRecordedChangeTimes() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);
            await db.Database.MigrateAsync(token);
            var now = DateTimeOffset.UtcNow;
            var agentId = Guid.CreateVersion7();
            var deviceId = Guid.CreateVersion7();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [security].[InventoryChanges] ([AgentId], [DeviceId], [Source], [Generation], [ObservedAt], [Kind], [Key], [AfterJson])
                VALUES ({agentId}, {deviceId}, 1, 1, {now}, 1, {"k"}, {"{}"})
                """, token);
            var recordedAt = await db.InventoryChanges.Select(x => x.RecordedAt).SingleAsync(token);
            Assert.IsTrue(recordedAt > now.AddMinutes(-1));
            var failure = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync("20261111205348_HostHardwareSources", token));
            Assert.AreEqual(51000, failure.Number);
            Assert.AreEqual(recordedAt, await db.InventoryChanges.Select(x => x.RecordedAt).SingleAsync(token));
        }, TestContext.CancellationToken);
}

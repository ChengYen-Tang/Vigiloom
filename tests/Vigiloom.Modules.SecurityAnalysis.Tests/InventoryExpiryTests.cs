using Microsoft.Data.SqlClient;
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
public sealed class InventoryExpiryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Expiry_ScansPastLockedAgentsWithoutStarvingLaterCandidates() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

            var now = DateTimeOffset.UtcNow;
            var owners = Enumerable.Range(0, 101).Select(_ => new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 0)).ToArray();
            foreach (var owner in owners)
            {
                var chunk = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
                    InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 1, 0, 2, new byte[32], []);
                db.InventorySources.Add(new InventorySourceState(owner, chunk.Source) { ActiveGeneration = 1 });
                db.InventorySnapshots.Add(new InventorySnapshot(owner, chunk, now.AddHours(-25)));
            }
            await db.SaveChangesAsync(token);
            var ordered = await db.InventorySnapshots.AsNoTracking().OrderBy(x => x.RowId).Select(x => x.AgentId).ToArrayAsync(token);
            await using var connection = new SqlConnection(database.ConnectionString);
            await connection.OpenAsync(token);
            await using var locked = await connection.BeginTransactionAsync(token);
            foreach (var agentId in ordered.Take(100))
                Assert.IsTrue(await InventoryAgentLock.TryAcquireAsync(connection, locked, agentId, 0, token));
            var expired = await new InventoryExpiry(new Factory(options)).RunAsync(token);
            Assert.AreEqual(1, expired);
            db.ChangeTracker.Clear();
            Assert.AreEqual(InventorySnapshotStatus.Expired,
                (await db.InventorySnapshots.AsNoTracking().SingleAsync(x => x.AgentId == ordered[100], token)).Status);
            await locked.RollbackAsync(token);
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task Expiry_ClosesOnlyStaleGenerationAndPreservesCurrentSnapshot() =>
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
            var baseline = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 1, 0, measured.Bytes,
                InventoryDigest.Combine([measured.Digest]), []);
            InventoryEntry[] items = [new("pkg", "pkg", "1", "x64")];
            var chunkMeasure = InventoryDigest.MeasureChunk(items);
            var pending = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 2,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 2, 2, 2L * chunkMeasure.Bytes,
                InventoryDigest.Combine([chunkMeasure.Digest, chunkMeasure.Digest]), items);
            var writer = new InventoryStageWriter();
            foreach (var chunk in new[] { baseline, pending })
            {
                await using var transaction = await connection.BeginTransactionAsync(token);
                Assert.AreEqual(InventoryStageResult.Accepted, await writer.StageAsync(connection, transaction, owner, chunk, now, token));
                await transaction.CommitAsync(token);
            }
            var expiry = new InventoryExpiry(new Factory(options));
            Assert.AreEqual(0, await expiry.RunAsync(token));
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE [security].[InventorySnapshots] SET [StartedAt] = TODATETIMEOFFSET(DATEADD(hour, -25, SYSUTCDATETIME()), '+00:00')
                WHERE [AgentId] = {owner.AgentId} AND [Generation] = 2
                """, token);
            Assert.AreEqual(1, await expiry.RunAsync(token));
            Assert.AreEqual(0, await expiry.RunAsync(token));
            var source = await db.InventorySources.AsNoTracking().SingleAsync(token);
            Assert.AreEqual(1L, source.CurrentGeneration);
            Assert.AreEqual(2L, source.ClosedGeneration);
            Assert.IsNull(source.ActiveGeneration);
            Assert.AreEqual(InventorySnapshotStatus.Expired, source.LastStatus);
            Assert.AreEqual(1, await db.InventoryChunks.CountAsync(token));
            await using var retry = await connection.BeginTransactionAsync(token);
            Assert.AreEqual(InventoryStageResult.Expired, await writer.StageAsync(connection, retry, owner, pending, now, token));
            await retry.RollbackAsync(token);
        }, TestContext.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }
}

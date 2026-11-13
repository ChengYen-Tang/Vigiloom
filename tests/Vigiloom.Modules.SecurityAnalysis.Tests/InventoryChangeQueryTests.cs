using System.Text.Json;
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
public sealed class InventoryChangeQueryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Query_PagesSameTimestampWithoutDuplicatesAndScopesDevice() =>
        Fixtures.ReusableSecurityDatabase.ExecuteAsync(async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150)
                    .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using var db = new SecurityAnalysisDbContext(options);

            var now = DateTimeOffset.UtcNow;
            var owner = new InventoryOwner(Guid.CreateVersion7(), Guid.CreateVersion7(), 0);
            var chunk = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 1, 0, 2, new byte[32], []);
            var snapshot = new InventorySnapshot(owner, chunk, now);
            for (var i = 0; i < 101; i++)
            {
                var item = new InventoryEntry($"p{i}", $"package-{i}", "1", "x64");
                db.InventoryChanges.Add(new InventoryChangeRecord(snapshot, now, (int)InventoryChangeType.Added, item.Key, null, JsonSerializer.Serialize(item)));
            }
            await db.SaveChangesAsync(token);
            var query = new InventoryChangeQuery(new Factory(options));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var pageCounts = new List<int>();
            do
            {
                var page = await query.GetAsync(owner.DeviceId, now.AddHours(-1), now.AddHours(1), InventorySource.LinuxDpkg, cursor, token);
                pageCounts.Add(page.Items.Count);
                foreach (var change in page.Items) Assert.IsTrue(seen.Add(change.After!.Key));
                cursor = page.NextCursor;
            } while (cursor is not null);
            CollectionAssert.AreEqual(new[] { 50, 50, 1 }, pageCounts);
            Assert.HasCount(101, seen);
            Assert.IsEmpty((await query.GetAsync(Guid.CreateVersion7(), now.AddHours(-1), now.AddHours(1), null, null, token)).Items);
            await Assert.ThrowsAsync<ArgumentException>(() => query.GetAsync(owner.DeviceId, now.AddHours(-1), now.AddHours(1), null, "invalid", token));
        }, TestContext.CancellationToken);

    private sealed class Factory(DbContextOptions<SecurityAnalysisDbContext> options) : IDbContextFactory<SecurityAnalysisDbContext>
    {
        public SecurityAnalysisDbContext CreateDbContext() => new(options);
    }
}

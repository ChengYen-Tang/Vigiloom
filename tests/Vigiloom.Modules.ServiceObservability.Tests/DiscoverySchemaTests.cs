using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class DiscoverySchemaTests(TestContext context)
{
    [AssemblyCleanup]
    public static void Cleanup() => SqlTestDatabase.CompleteRun();

    [TestMethod]
    public Task Migration_SeparatesSourceKeyAndEnvironmentAndRejectsDuplicates() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var first = new ServiceSource("first", new byte[32], now);
            var second = new ServiceSource("second", new byte[32], now);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.AddRange(first, second);
                db.Candidates.AddRange(
                    new ServiceCandidate(first.Id, "orders", "prod", "Orders", now),
                    new ServiceCandidate(first.Id, "Orders", "prod", "Orders", now),
                    new ServiceCandidate(first.Id, "orders", "test", "Orders", now),
                    new ServiceCandidate(second.Id, "orders", "prod", "Orders", now));
                await db.SaveChangesAsync(token);
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                Assert.AreEqual(4, await db.Candidates.CountAsync(token));
                db.Candidates.Add(new ServiceCandidate(first.Id, " orders ", " prod ", "Duplicate", now));
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            }
        }, context.CancellationToken);
}

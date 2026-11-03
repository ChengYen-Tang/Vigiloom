using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Application;
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
    public Task SourceCredentials_RejectWrongTypeAndRevokedSecret() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(options)) await db.Database.MigrateAsync(token);
            var credentials = new SourceCredentials(new Factory(options), TimeProvider.System);
            var issued = await credentials.IssueAsync("collector", token);
            Assert.AreEqual(issued.SourceId, await credentials.AuthenticateAsync(issued.Secret, token));
            var altered = issued.Secret[..41] + (issued.Secret[41] == 'A' ? 'B' : 'A') + issued.Secret[42..];
            Assert.IsNull(await credentials.AuthenticateAsync(altered, token));
            Assert.IsNull(await credentials.AuthenticateAsync("vla_" + issued.Secret[4..], token));
            Assert.IsNull(await credentials.AuthenticateAsync(issued.Secret + "=", token));
            Assert.IsNull(await credentials.AuthenticateAsync($"vls_{Guid.CreateVersion7():D}.{issued.Secret[41..]}", token));
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            var finalIndex = alphabet.IndexOf(issued.Secret[^1]);
            var nonCanonical = issued.Secret[..^1] + alphabet[finalIndex + 1];
            Assert.IsNull(await credentials.AuthenticateAsync(nonCanonical, token));
            Assert.IsTrue(await credentials.RevokeAsync(issued.SourceId, token));
            Assert.IsNull(await credentials.AuthenticateAsync(issued.Secret, token));
            Assert.IsFalse(await credentials.RevokeAsync(Guid.CreateVersion7(), token));
        }, context.CancellationToken);

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

    private sealed class Factory(DbContextOptions<ServiceObservabilityDbContext> options) : IDbContextFactory<ServiceObservabilityDbContext>
    {
        public ServiceObservabilityDbContext CreateDbContext() => new(options);
    }
}

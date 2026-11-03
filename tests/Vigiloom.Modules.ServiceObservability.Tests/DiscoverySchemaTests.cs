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
    [DataRow(false)]
    [DataRow(true)]
    public Task CandidateDiscovery_LastSourceSlotIsSerializedAndExistingReportsStillUpdate(bool snapshotReads) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.ExecuteSqlRawAsync(snapshotReads ? "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE" : "ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE", token);
                await db.Database.MigrateAsync(token);
            }
            var factory = new Factory(options);
            var source = await new SourceCredentials(factory, TimeProvider.System).IssueAsync("source", token);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var now = DateTimeOffset.UtcNow;
                db.Candidates.AddRange(Enumerable.Range(0, CandidateDiscovery.PerSourceLimit - 1)
                    .Select(i => new ServiceCandidate(source.SourceId, $"key-{i}", "prod", "Service", now)));
                await db.SaveChangesAsync(token);
            }
            var discovery = new CandidateDiscovery(factory);
            var outcomes = await Task.WhenAll(
                discovery.ObserveAsync(source.SourceId, "final-a", "prod", "A", token),
                discovery.ObserveAsync(source.SourceId, "final-b", "prod", "B", token));
            Assert.AreEqual(1, outcomes.Count(x => x == CandidateObservation.Recorded));
            Assert.AreEqual(1, outcomes.Count(x => x == CandidateObservation.CapacityReached));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "key-0", "prod", "Updated", token));
            await using var verify = new ServiceObservabilityDbContext(options);
            Assert.AreEqual(CandidateDiscovery.PerSourceLimit, await verify.Candidates.CountAsync(token));
            Assert.AreEqual("Updated", (await verify.Candidates.SingleAsync(x => x.SourceId == source.SourceId && x.ServiceKey == "key-0", token)).DisplayName);
        }, context.CancellationToken);

    [TestMethod]
    public Task CandidateDiscovery_UsesSourceIdentityAndRejectsRevokedSources() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            await using (var db = new ServiceObservabilityDbContext(options)) await db.Database.MigrateAsync(token);
            var factory = new Factory(options);
            var credentials = new SourceCredentials(factory, TimeProvider.System);
            var first = await credentials.IssueAsync("first", token);
            var second = await credentials.IssueAsync("second", token);
            var discovery = new CandidateDiscovery(factory);
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(first.SourceId, "orders", "prod", "Orders", token));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(first.SourceId, "orders", "prod", "New name", token));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(second.SourceId, "orders", "prod", "Orders", token));
            var concurrent = await Task.WhenAll(
                discovery.ObserveAsync(second.SourceId, "payments", "prod", "Payments", token),
                discovery.ObserveAsync(second.SourceId, "payments", "prod", "Payments", token));
            Assert.IsTrue(concurrent.All(x => x == CandidateObservation.Recorded));
            var candidates = await discovery.ListAsync(1, token);
            Assert.HasCount(3, candidates);
            Assert.AreEqual("New name", candidates.Single(x => x.SourceId == first.SourceId).DisplayName);
            Assert.IsTrue(await credentials.RevokeAsync(first.SourceId, token));
            Assert.AreEqual(CandidateObservation.SourceUnavailable, await discovery.ObserveAsync(first.SourceId, "new", "prod", "New", token));
            Assert.HasCount(2, await discovery.ListAsync(1, token));
        }, context.CancellationToken);

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

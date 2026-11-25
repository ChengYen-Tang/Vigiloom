using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.Resources.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ApplicationDiscoveryTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task CandidateRemainsVisibleWhileReportingAndDisappearsAfterThreeMinutes() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
            await using (var db = new ResourcesDbContext(options)) await db.Database.MigrateAsync(token);
            var factory = new PooledDbContextFactory<ResourcesDbContext>(options);
            var discovery = new ApplicationDiscovery(factory);
            var observation = new ApplicationObservation(Guid.CreateVersion7(), "orders", "production", "Orders", "company");
            Assert.AreEqual(ApplicationObservationStatus.Recorded, await discovery.ObserveAsync(observation, token));
            var first = (await discovery.ListAsync(1, token)).Single();
            Assert.AreEqual(ApplicationObservationStatus.Recorded, await discovery.ObserveAsync(observation with { DisplayName = "Orders v2" }, token));
            var updated = (await discovery.ListAsync(1, token)).Single();
            Assert.AreEqual(first.Id, updated.Id);
            Assert.AreEqual("Orders v2", updated.DisplayName);
            await using (var db = new ResourcesDbContext(options))
                await db.ApplicationCandidates.Where(x => x.Id == first.Id)
                    .ExecuteUpdateAsync(x => x.SetProperty(c => c.LastSeen, DateTimeOffset.UtcNow.AddMinutes(-4)), token);
            Assert.IsEmpty(await discovery.ListAsync(1, token));
            await discovery.ObserveAsync(observation, token);
            Assert.HasCount(1, await discovery.ListAsync(1, token));
            await using (var db = new ResourcesDbContext(options))
            {
                var candidate = await db.ApplicationCandidates.SingleAsync(token);
                db.ApplicationServices.Add(new ApplicationService(candidate, DateTimeOffset.UtcNow));
                db.ApplicationCandidates.Remove(candidate);
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(ApplicationObservationStatus.AlreadyApproved, await discovery.ObserveAsync(observation, token));
            Assert.IsEmpty(await discovery.ListAsync(1, token));
        }, TestContext.CancellationToken);
}

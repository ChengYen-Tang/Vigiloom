using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
public sealed class HealthTargetTests(TestContext context)
{
    [TestMethod]
    [DataRow("file:///etc/passwd")]
    [DataRow("https://user:secret@example.test/health")]
    [DataRow("https://example.test/health#fragment")]
    public void Constructor_RejectsUnsupportedOrSecretBearingEndpoint(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => new HealthTarget(Guid.CreateVersion7(), null, endpoint, 30, 5, 3, 2, DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void Constructor_AllowsNonSecretQueryForFiltering()
    {
        var target = new HealthTarget(Guid.CreateVersion7(), null, "https://example.test/health?tag=ready", 30, 5, 3, 2, DateTimeOffset.UtcNow);
        Assert.AreEqual("https://example.test/health?tag=ready", target.Endpoint);
    }

    [TestMethod]
    public void Apply_InvalidSettings_DoesNotModifyExistingConfiguration()
    {
        var target = new HealthTarget(Guid.CreateVersion7(), null, "https://example.test/health", 30, 5, 3, 2, DateTimeOffset.UtcNow);
        Assert.Throws<ArgumentOutOfRangeException>(() => target.Apply("https://other.test/health", 5, 6, 3, 2, DateTimeOffset.UtcNow));
        Assert.AreEqual("https://example.test/health", target.Endpoint);
        Assert.AreEqual(30, target.IntervalSeconds);
        Assert.IsFalse(target.Enabled);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("Database")]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Migration_PersistsTargetAndRejectsAnotherServicesInstance() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var now = DateTimeOffset.UtcNow;
            var source = new ServiceSource("source", new byte[32], now);
            var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            var other = new MonitoredService(new ServiceCandidate(source.Id, "billing", "prod", "Billing", now), now);
            var instanceId = Guid.CreateVersion7();
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                await db.Database.MigrateAsync(token);
                db.Sources.Add(source);
                db.Services.AddRange(service, other);
                await db.SaveChangesAsync(token);
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({instanceId},{service.Id},{source.Id},{"node"},{now},{now})", token);
                db.HealthTargets.Add(new HealthTarget(service.Id, instanceId, "https://example.test/health", 30, 5, 3, 2, now));
                await db.SaveChangesAsync(token);
            }
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var saved = await db.HealthTargets.AsNoTracking().SingleAsync(token);
                Assert.AreEqual(service.Id, saved.ServiceId);
                Assert.AreEqual(instanceId, saved.InstanceId);
                Assert.AreEqual(7, saved.Id.Version);
                Assert.IsFalse(saved.Enabled);
                Assert.HasCount(8, saved.Version);
                var indexes = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE [object_id] = OBJECT_ID(N'[service].[HealthTargets]') AND (([is_primary_key] = 1 AND [type] = 2) OR ([name] = N'IX_HealthTargets_RowId' AND [type] = 1 AND [is_unique] = 1))").SingleAsync(token);
                Assert.AreEqual(2, indexes);
                var identity = await db.Database.SqlQueryRaw<int>("SELECT CONVERT(int, COLUMNPROPERTY(OBJECT_ID(N'[service].[HealthTargets]'), N'RowId', 'IsIdentity')) AS [Value]").SingleAsync(token);
                Assert.AreEqual(1, identity);
                await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() =>
                    db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthTargets] SET [TimeoutSeconds] = 61 WHERE [Id] = {saved.Id}", token));
                db.HealthTargets.Add(new HealthTarget(other.Id, instanceId, "https://example.test/health", 30, 5, 3, 2, now));
                await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
            }
            await using var first = new ServiceObservabilityDbContext(options);
            await using var second = new ServiceObservabilityDbContext(options);
            var firstCopy = await first.HealthTargets.SingleAsync(token);
            var staleCopy = await second.HealthTargets.SingleAsync(token);
            firstCopy.SetEnabled(true, now.AddMinutes(1));
            await first.SaveChangesAsync(token);
            staleCopy.SetEnabled(true, now.AddMinutes(2));
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(token));
        }, context.CancellationToken);
}

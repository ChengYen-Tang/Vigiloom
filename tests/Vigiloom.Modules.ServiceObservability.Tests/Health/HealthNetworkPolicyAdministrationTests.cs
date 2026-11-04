using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HealthNetworkPolicyAdministrationTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Update_SerializesVersionsAndPreservesRevisionWithAudit() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
            var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
            await using (var db = new ServiceObservabilityDbContext(options)) await db.Database.MigrateAsync(token);
            await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
            var factory = new PooledDbContextFactory<ServiceObservabilityDbContext>(options);
            var administration = new HealthNetworkPolicyAdministration(factory, new OperationAuditWriter());
            var actor = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var empty = await administration.GetAsync(token);
            Assert.IsEmpty(empty.Networks);
            Assert.AreEqual(0L, empty.Generation);
            var created = await administration.UpdateAsync(new(["10.0.0.0/8"], empty.Version), actor, token);
            Assert.IsNotNull(created);
            Assert.AreEqual(1L, created.Generation);
            var outcomes = await Task.WhenAll(
                administration.UpdateAsync(new(["192.168.1.0/24"], created.Version), actor, token),
                administration.UpdateAsync(new(["172.16.0.0/12"], created.Version), actor, token));
            Assert.AreEqual(1, outcomes.Count(x => x is not null));
            var current = (await administration.GetAsync(token));
            Assert.AreEqual(2L, current.Generation);
            var failing = new HealthNetworkPolicyAdministration(factory, new FailingAudit());
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.UpdateAsync(new([], current.Version), actor, token));
            Assert.AreEqual(current.Version, (await administration.GetAsync(token)).Version);
            await using (var db = new ServiceObservabilityDbContext(options))
            {
                var revisions = await db.HealthNetworkRevisions.OrderBy(x => x.Generation).ToArrayAsync(token);
                Assert.HasCount(2, revisions);
                Assert.AreEqual(actor, revisions[0].ActorId);
                Assert.Contains("10.0.0.0/8", revisions[0].NetworksJson);
                Assert.AreEqual(current.Generation, revisions[1].Generation);
            }
            var audit = new OperationAuditQueryService(new PooledDbContextFactory<OperationsDbContext>(operations));
            var history = await audit.QueryHealthNetworkPoliciesAsync(new(now.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)), token);
            Assert.HasCount(2, history.Items);
            Assert.AreEqual(2L, history.Items[0].HealthNetworkPolicy!.CurrentGeneration);
        }, context.CancellationToken);

    private sealed class FailingAudit : IOperationAuditWriter<OperationAudit>
    {
        public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken) =>
            Task.FromException(new InvalidOperationException("Simulated audit failure"));
    }
}

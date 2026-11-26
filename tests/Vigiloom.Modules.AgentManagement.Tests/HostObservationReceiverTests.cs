using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostObservationReceiverTests(TestContext context)
{
    [TestMethod]
    public Task StoresAndDeduplicatesBatchesAndTheirItems() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            var factory = new PooledDbContextFactory<HostMonitoringDbContext>(options);
            await using (var db = await factory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var receiver = new HostObservationReceiver(factory, TimeProvider.System);
            var agentId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var item = new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.CpuTotalPercent, "cpu", null, 42);
            var batch = new AgentMetricBatch(AgentMetricBatchCodec.SchemaVersion, Guid.CreateVersion7(), now, [item]);
            var data = AgentMetricBatchCodec.Serialize(batch);
            Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(agentId, data, token));
            Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(agentId, data, token));
            var changed = AgentMetricBatchCodec.Serialize(batch with { Items = [item with { Value = 43 }] });
            Assert.AreEqual(BatchReceiptStatus.Conflict, await receiver.ReceiveAsync(agentId, changed, token));
            var repeatedItem = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7() });
            Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(agentId, repeatedItem, token));
            var conflictingItem = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7(), Items = [item with { Value = 43 }] });
            Assert.AreEqual(BatchReceiptStatus.Conflict, await receiver.ReceiveAsync(agentId, conflictingItem, token));
            var concurrent = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7() });
            var results = await Task.WhenAll(receiver.ReceiveAsync(agentId, concurrent, token), receiver.ReceiveAsync(agentId, concurrent, token));
            CollectionAssert.AreEquivalent(new[] { BatchReceiptStatus.Accepted, BatchReceiptStatus.AlreadyAccepted }, results);
            var newItem = item with { MessageId = Guid.CreateVersion7() };
            var first = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7(), Items = [newItem] });
            var second = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7(), Items = [newItem] });
            var repeatedResults = await Task.WhenAll(receiver.ReceiveAsync(agentId, first, token), receiver.ReceiveAsync(agentId, second, token));
            Assert.IsTrue(repeatedResults.All(result => result == BatchReceiptStatus.Accepted));
            await using var snapshot = await factory.CreateDbContextAsync(token);
            Assert.AreEqual(2, await snapshot.HostMetrics.CountAsync(token));
            Assert.AreEqual(5, await snapshot.ReceivedBatches.CountAsync(token));
        }, context.CancellationToken);
}

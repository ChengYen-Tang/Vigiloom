using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
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
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                var policy = new AgentReplayPolicy();
                var policyTime = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
                policy.InitializeFresh(policyTime);
                policy.BeginCleanupCutover(policyTime);
                db.ReplayPolicies.Add(policy);
                await db.SaveChangesAsync(token);
            }
            var receiver = new HostObservationReceiver(factory, TimeProvider.System);
            var agentId = Guid.CreateVersion7();
            var identity = new RegisteredAgentIdentity(agentId, Guid.CreateVersion7(), 1);
            var now = DateTimeOffset.UtcNow;
            var item = new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.CpuTotalPercent, "cpu", null, 42);
            var batch = new AgentMetricBatch(AgentMetricBatchCodec.SchemaVersion, Guid.CreateVersion7(), now, [item]);
            var data = AgentMetricBatchCodec.Serialize(batch);
            Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(identity, data, token));
            Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(identity, data, token));
            Assert.AreEqual(BatchReceiptStatus.Unauthorized, await receiver.ReceiveAsync(identity with { DeviceGeneration = 2 }, data, token));
            var expired = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7(), EnqueuedAt = now.AddDays(-2) });
            Assert.AreEqual(BatchReceiptStatus.Expired, await receiver.ReceiveAsync(identity, expired, token));
            await using (var rejected = await factory.CreateDbContextAsync(token))
            {
                var state = await rejected.InboxStates.SingleAsync(token);
                Assert.AreEqual(AgentReplayIssue.Expired, state.LastReplayIssue);
                Assert.IsNotNull(state.ReplayClockFloor);
            }
            var future = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7(), EnqueuedAt = now.AddMinutes(10) });
            Assert.AreEqual(BatchReceiptStatus.TimestampAhead, await receiver.ReceiveAsync(identity, future, token));
            var changed = AgentMetricBatchCodec.Serialize(batch with { Items = [item with { Value = 43 }] });
            Assert.AreEqual(BatchReceiptStatus.Conflict, await receiver.ReceiveAsync(identity, changed, token));
            var repeatedItem = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7() });
            Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(identity, repeatedItem, token));
            var conflictingItem = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7(), Items = [item with { Value = 43 }] });
            Assert.AreEqual(BatchReceiptStatus.Conflict, await receiver.ReceiveAsync(identity, conflictingItem, token));
            var concurrent = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7() });
            var results = await Task.WhenAll(receiver.ReceiveAsync(identity, concurrent, token), receiver.ReceiveAsync(identity, concurrent, token));
            CollectionAssert.AreEquivalent(new[] { BatchReceiptStatus.Accepted, BatchReceiptStatus.AlreadyAccepted }, results);
            var newItem = item with { MessageId = Guid.CreateVersion7() };
            var first = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7(), Items = [newItem] });
            var second = AgentMetricBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7(), Items = [newItem] });
            var repeatedResults = await Task.WhenAll(receiver.ReceiveAsync(identity, first, token), receiver.ReceiveAsync(identity, second, token));
            Assert.IsTrue(repeatedResults.All(result => result == BatchReceiptStatus.Accepted));
            await using var snapshot = await factory.CreateDbContextAsync(token);
            Assert.AreEqual(2, await snapshot.HostMetrics.CountAsync(token));
            Assert.AreEqual(5, await snapshot.ReceivedBatches.CountAsync(token));
            Assert.IsTrue(await snapshot.ReceivedBatches.AllAsync(x => x.TrustedReceivedAt != null && x.RetainUntil != null, token));
        }, context.CancellationToken);
}

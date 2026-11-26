using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostSecurityBatchTests(TestContext context)
{
    [TestMethod]
    public Task CommitsReceiptPayloadAndDeliveryTogether() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            var factory = new PooledDbContextFactory<HostMonitoringDbContext>(options);
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
                var policy = new AgentReplayPolicy();
                policy.InitializeFresh(now);
                policy.BeginCleanupCutover(now);
                db.ReplayPolicies.Add(policy);
                await db.SaveChangesAsync(token);
            }
            await using (var scheduling = new SchedulingDbContext(schedulingOptions)) await scheduling.Database.MigrateAsync(token);
            var writer = new HostSecurityWorkWriter(new TemporalWorkWriter(new OutboxWriter([new TestOutboxHandler()])),
                new ConfigurationBuilder().AddInMemoryCollection().Build());
            var receiver = new HostObservationReceiver(factory, TimeProvider.System, security: writer);
            var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var nowUtc = DateTimeOffset.UtcNow;
            var chunk = new InventoryChunk(InventoryChunkCodec.SchemaVersion, Guid.CreateVersion7(), nowUtc,
                Guid.CreateVersion7(), 1, InventorySource.LinuxDpkg, nowUtc, InventoryOutcome.Unsupported,
                0, 1, 0, 2, SHA256.HashData("[]"u8), []);
            var payload = InventoryChunkCodec.Serialize(chunk);
            Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(identity, payload, token));
            Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(identity, payload, token));
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                var stored = await db.HostSecurityBatches.SingleAsync(token);
                CollectionAssert.AreEqual(payload, stored.Payload);
                Assert.AreEqual(chunk.BatchId, stored.BatchId);
                Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM scheduling.Outbox").SingleAsync(token));
            }
            var failing = new HostObservationReceiver(factory, TimeProvider.System, security: new FailingWriter());
            var failed = chunk with { BatchId = Guid.CreateVersion7() };
            await Assert.ThrowsAsync<InvalidOperationException>(() => failing.ReceiveAsync(identity, InventoryChunkCodec.Serialize(failed), token));
            await using var check = await factory.CreateDbContextAsync(token);
            Assert.IsFalse(await check.ReceivedBatches.AnyAsync(x => x.BatchId == failed.BatchId, token));
            Assert.IsFalse(await check.HostSecurityBatches.AnyAsync(x => x.BatchId == failed.BatchId, token));
            Assert.AreEqual(1, await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM scheduling.Outbox").SingleAsync(token));
            var old = nowUtc.AddDays(-40);
            await check.Database.ExecuteSqlInterpolatedAsync($"UPDATE scheduling.Outbox SET State = 2, CompletedAt = {old}", token);
            var pendingId = Guid.CreateVersion7();
            await check.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO scheduling.Outbox (MessageId, MessageType, Payload, CreatedAt, AvailableAt, State, Attempts) VALUES ({pendingId}, N'temporal.service-work', N'{{}}', {old}, {old}, 0, 0)", token);
            Assert.AreEqual(1, await new HostWorkRetention(factory, new RetentionDays()).RunAsync(token));
            Assert.AreEqual(1, await check.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM scheduling.Outbox WHERE State = 0").SingleAsync(token));
        }, context.CancellationToken);

    private sealed class FailingWriter : IHostSecurityWorkWriter
    {
        public Task WriteAsync(HostMonitoringDbContext db, HostSecurityBatchReady batch, CancellationToken token) =>
            throw new InvalidOperationException("Simulated write failure.");
    }

    private sealed class RetentionDays : IHostWorkRetentionDays
    {
        public Task<int> GetAsync(CancellationToken token) => Task.FromResult(30);
    }

    private sealed class TestOutboxHandler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostInboxCleanupTests(TestContext context)
{
    [TestMethod]
    public Task RemovesOnlyIdentitiesAbsentFromOperationsAndSkipsPendingAgents() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            var factory = new PooledDbContextFactory<HostMonitoringDbContext>(options);
            var agent = Guid.CreateVersion7();
            var pendingAgent = Guid.CreateVersion7();
            var retained = Guid.CreateVersion7();
            var removed = Guid.CreateVersion7();
            var pendingId = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                db.InboxStates.AddRange(new HostInboxState { Id = agent, DeviceId = Guid.CreateVersion7(), DeviceGeneration = 1 },
                    new HostInboxState { Id = pendingAgent, DeviceId = Guid.CreateVersion7(), DeviceGeneration = 1 });
                db.HostLogMessageIds.AddRange(new HostLogMessageId { AgentId = agent, MessageId = retained, ReceivedAt = now },
                    new HostLogMessageId { AgentId = agent, MessageId = removed, ReceivedAt = now },
                    new HostLogMessageId { AgentId = pendingAgent, MessageId = pendingId, ReceivedAt = now });
                var receipt = new ReceivedBatch(pendingAgent, Guid.CreateVersion7(), new byte[32], now, now, 1, 1);
                db.ReceivedBatches.Add(receipt);
                db.Entry(receipt).Property("PendingDelivery").CurrentValue = true;
                await db.SaveChangesAsync(token);
            }
            var reader = new ExistingLogIds(retained);
            Assert.AreEqual(1, await new HostLogIdentityCleanup(factory, reader).RunAsync(token));
            await using var snapshot = await factory.CreateDbContextAsync(token);
            Assert.IsTrue(await snapshot.HostLogMessageIds.AnyAsync(x => x.MessageId == retained, token));
            Assert.IsFalse(await snapshot.HostLogMessageIds.AnyAsync(x => x.MessageId == removed, token));
            Assert.IsTrue(await snapshot.HostLogMessageIds.AnyAsync(x => x.MessageId == pendingId, token));
        }, context.CancellationToken);

    private sealed class ExistingLogIds(Guid retained) : IHostLogIdentities
    {
        public Task<IReadOnlyCollection<Guid>> ExistingAsync(Guid agentId, IReadOnlyCollection<Guid> messageIds, CancellationToken token) =>
            Task.FromResult<IReadOnlyCollection<Guid>>(messageIds.Contains(retained) ? [retained] : []);
    }

    [TestMethod]
    public Task RemovesExpiredReceiptsAndPreservesReplayFence() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            var factory = new PooledDbContextFactory<HostMonitoringDbContext>(options);
            var agent = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var old = now.AddDays(-40);
            await using (var db = await factory.CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                var policy = new AgentReplayPolicy();
                policy.InitializeFresh(old);
                policy.BeginCleanupCutover(old);
                db.ReplayPolicies.Add(policy);
                db.InboxStates.Add(new HostInboxState { Id = agent, DeviceId = Guid.CreateVersion7(), DeviceGeneration = 1 });
                var accepted = new ReceivedBatch(agent, Guid.CreateVersion7(), new byte[32], old, old, 1, 20, old.AddMinutes(1), old.AddDays(7));
                var pending = new ReceivedBatch(agent, Guid.CreateVersion7(), new byte[32], old, old, 1, 30, old.AddMinutes(1), old.AddDays(7));
                var recent = new ReceivedBatch(agent, Guid.CreateVersion7(), new byte[32], now, now, 1, 40, now, now.AddDays(7));
                var security = new ReceivedBatch(agent, Guid.CreateVersion7(), new byte[32], old, old, 1, 50, old.AddMinutes(1), old.AddDays(7));
                db.ReceivedBatches.AddRange(accepted, pending, recent, security);
                db.HostSecurityBatches.Add(new HostSecurityBatch { AgentId = agent, BatchId = security.BatchId, DeviceId = Guid.CreateVersion7(),
                    DeviceGeneration = 1, Kind = "inventory", Payload = [1], ReceivedAt = old });
                db.Entry(pending).Property("PendingDelivery").CurrentValue = true;
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(2, await new HostInboxCleanup(factory).RunAsync(token));
            await using var snapshot = await factory.CreateDbContextAsync(token);
            Assert.AreEqual(2, await snapshot.ReceivedBatches.CountAsync(token));
            var state = await snapshot.InboxStates.SingleAsync(token);
            Assert.AreEqual(2L, state.InboxReceiptCount);
            Assert.AreEqual(90L, state.InboxReceiptPayloadBytes);
            Assert.AreEqual(2L, state.InboxDeletedTotal);
            Assert.IsNotNull(state.ReplayCleanedBefore);
            await snapshot.HostSecurityBatches.ExecuteDeleteAsync(token);
            Assert.AreEqual(1, await new HostInboxCleanup(factory).RunAsync(token));
        }, context.CancellationToken);
}

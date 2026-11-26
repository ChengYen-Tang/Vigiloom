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
                db.ReceivedBatches.AddRange(accepted, pending, recent);
                db.Entry(pending).Property("PendingDelivery").CurrentValue = true;
                await db.SaveChangesAsync(token);
            }
            Assert.AreEqual(2, await new HostInboxCleanup(factory).RunAsync(token));
            await using var snapshot = await factory.CreateDbContextAsync(token);
            Assert.AreEqual(1, await snapshot.ReceivedBatches.CountAsync(token));
            var state = await snapshot.InboxStates.SingleAsync(token);
            Assert.AreEqual(1L, state.InboxReceiptCount);
            Assert.AreEqual(40L, state.InboxReceiptPayloadBytes);
            Assert.AreEqual(2L, state.InboxDeletedTotal);
            Assert.IsNotNull(state.ReplayCleanedBefore);
        }, context.CancellationToken);
}

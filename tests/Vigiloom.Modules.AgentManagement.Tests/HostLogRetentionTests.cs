using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AgentManagement.Application.Retention;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostLogRetentionTests(TestContext context)
{
    [TestMethod]
    public Task CleanupKeepsPendingEventsAndCorrelationWindow() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var resourcesOptions = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
            var agentsOptions = new DbContextOptionsBuilder<AgentManagementDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options;
            var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new ResourcesDbContext(resourcesOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new AgentManagementDbContext(agentsOptions)) await db.Database.MigrateAsync(token);
            await using (var db = new SchedulingDbContext(schedulingOptions)) await db.Database.MigrateAsync(token);

            var now = DateTimeOffset.UtcNow;
            var firstDevice = new Device("retention one", "127.0.0.1", true, now);
            var secondDevice = new Device("retention two", "127.0.0.2", true, now);
            await using (var db = new ResourcesDbContext(resourcesOptions)) { db.Devices.AddRange(firstDevice, secondDevice); await db.SaveChangesAsync(token); }
            var first = new Agent(firstDevice.Id, 1, new byte[32], now);
            var second = new Agent(secondDevice.Id, 1, new byte[32], now);
            var pending = Guid.CreateVersion7(); var leased = Guid.CreateVersion7(); var completed = Guid.CreateVersion7();
            var old = Enumerable.Range(0, 503).Select(_ => new AgentLogRecord(Guid.CreateVersion7(), now.AddDays(-20), "old", null, null, null, null, null)).ToList();
            old.AddRange([new(pending, now.AddDays(-20), "pending", null, null, null, null, null),
                new(leased, now.AddDays(-20), "leased", null, null, null, null, null),
                new(completed, now.AddDays(-20), "completed", null, null, null, null, null)]);
            var batch = new AgentLogBatch(3, Guid.CreateVersion7(), now.AddDays(-20), Guid.CreateVersion7(), AgentLogSourceKind.Journal, "system", 1, old);
            var recent = batch with { BatchId = Guid.CreateVersion7(), Items = [new(Guid.CreateVersion7(), now.AddDays(-20), "recent receipt", null, null, null, null, null)] };
            await using (var db = new AgentManagementDbContext(agentsOptions))
            {
                db.Agents.AddRange(first, second);
                for (var i = 0; i < old.Count; i++) db.HostLogs.Add(new HostLog(first.Id, batch, i, now.AddDays(-20)));
                db.HostLogs.Add(new HostLog(second.Id, batch with { BatchId = Guid.CreateVersion7() }, 503, now.AddDays(-20)));
                db.HostLogs.Add(new HostLog(first.Id, recent, 0, now.AddDays(-2)));
                await db.SaveChangesAsync(token);
                foreach (var (id, state) in new[] { (pending, 0), (leased, 1), (completed, 2) })
                {
                    var payload = JsonSerializer.Serialize(new HostLogDispatch(first.Id, firstDevice.Id, 1, [id]));
                    await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO scheduling.Outbox (MessageId, MessageType, Payload, CreatedAt, AvailableAt, State, Attempts) VALUES ({Guid.CreateVersion7()}, {HostLogDispatch.MessageType}, {payload}, {now}, {now}, {state}, {0})", token);
                }
            }

            var cleanup = new HostLogRetention();
            await using (var db = new AgentManagementDbContext(agentsOptions))
            {
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var connection = db.Database.GetDbConnection(); var transaction = tx.GetDbTransaction();
                Assert.AreEqual(505L, await cleanup.CountAsync(connection, transaction, now, 1, token));
                Assert.AreEqual(500, await cleanup.DeleteBatchAsync(connection, transaction, now, 1, 500, token));
                Assert.AreEqual(5, await cleanup.DeleteBatchAsync(connection, transaction, now, 1, 500, token));
                Assert.AreEqual(0, await cleanup.DeleteBatchAsync(connection, transaction, now, 1, 500, token));
                await tx.CommitAsync(token);
            }
            await using (var db = new AgentManagementDbContext(agentsOptions))
            {
                var remaining = await db.HostLogs.Select(x => x.MessageId).ToArrayAsync(token);
                CollectionAssert.AreEquivalent(new[] { pending, leased, recent.Items[0].MessageId }, remaining);
                await db.Database.ExecuteSqlRawAsync("UPDATE scheduling.Outbox SET State = 2 WHERE State IN (0, 1)", token);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                Assert.AreEqual(2, await cleanup.DeleteBatchAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), now, 1, 500, token));
                await tx.CommitAsync(token);
            }
        }, context.CancellationToken);
}

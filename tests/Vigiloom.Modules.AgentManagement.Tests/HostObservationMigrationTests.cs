using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Migrator.HostMonitoring;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostObservationMigrationTests(TestContext context)
{
    [TestMethod]
    public Task CopiesObservationsAndReceiptsIntoIndependentDatabase() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (source, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            var resourceOptions = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(source.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
            var legacyOptions = new DbContextOptionsBuilder<AgentManagementDbContext>().UseSqlServer(source.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options;
            var hostOptions = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(target.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            await using (var resources = new ResourcesDbContext(resourceOptions)) await resources.Database.MigrateAsync(token);
            await using (var legacy = new AgentManagementDbContext(legacyOptions)) await legacy.Database.MigrateAsync(token);

            var now = DateTimeOffset.UtcNow;
            var device = new Device("server", "127.0.0.1", true, now);
            await using (var resources = new ResourcesDbContext(resourceOptions))
            {
                resources.Devices.Add(device);
                await resources.SaveChangesAsync(token);
            }
            var agent = new Agent(device.Id, 1, new byte[32], now);
            var batchId = Guid.CreateVersion7();
            var messageId = Guid.CreateVersion7();
            await using (var legacy = new AgentManagementDbContext(legacyOptions))
            {
                legacy.Agents.Add(agent);
                legacy.ReceivedBatches.Add(new ReceivedBatch(agent.Id, batchId, new byte[32], now, now, 1, 100));
                var point = new HostMetric(agent.Id, batchId, 0,
                    new AgentMetricPoint(messageId, now, AgentMetricKind.CpuTotalPercent, "cpu", null, 42), now);
                var rollup = new HostMetricRollup(point, now);
                rollup.Add(point);
                legacy.HostMetrics.Add(point);
                legacy.HostMetricRollups.Add(rollup);
                legacy.RuntimeObservations.Add(new RuntimeObservation(agent.Id, Guid.CreateVersion7(), 0,
                    new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "Windows", "x64", "10"), now));
                legacy.HostMetricRetentionSettings.Add(new HostMetricRetentionSetting { RawDays = 14, RollupDays = 90 });
                await legacy.SaveChangesAsync(token);
            }
            await HostMonitoringDatabaseCommand.RunAsync(target.ConnectionString, source.ConnectionString, token);
            await using var copied = new HostMonitoringDbContext(hostOptions);
            Assert.IsTrue(await copied.ReceivedBatches.AnyAsync(x => x.AgentId == agent.Id && x.BatchId == batchId, token));
            Assert.IsTrue(await copied.HostMetrics.AnyAsync(x => x.AgentId == agent.Id && x.MessageId == messageId && x.Value == 42, token));
            var copiedRollup = await copied.HostMetricRollups.SingleAsync(token);
            Assert.AreEqual(42d, copiedRollup.Sum);
            Assert.AreEqual(1L, copiedRollup.Count);
            Assert.AreEqual("Windows", (await copied.RuntimeObservations.SingleAsync(token)).OperatingSystem);
            Assert.AreEqual(14, (await copied.HostMetricRetentionSettings.SingleAsync(token)).RawDays);
        }, outerToken), context.CancellationToken);
}

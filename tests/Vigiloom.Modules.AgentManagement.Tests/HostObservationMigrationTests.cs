using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    public Task RecordsHeartbeatSessionsInHostDatabase() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            await HostMonitoringDatabaseCommand.RunAsync(target.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(target.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            var factory = new PooledDbContextFactory<HostMonitoringDbContext>(options);
            var heartbeat = new HostHeartbeat(factory, TimeProvider.System);
            var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var epoch = Guid.CreateVersion7();
            Assert.IsNull((await heartbeat.GetSessionAsync(identity, token))!.Epoch);
            Assert.AreEqual(AgentIdentityStatus.Success, await heartbeat.BeginSessionAsync(identity, new(null, epoch), token));
            Assert.AreEqual(AgentIdentityStatus.Success, await heartbeat.BeginSessionAsync(identity, new(null, epoch), token));
            Assert.AreEqual(AgentIdentityStatus.Success, await heartbeat.RecordAsync(identity, new AgentHeartbeatReport(1, epoch, 2, null), token));
            Assert.AreEqual(AgentIdentityStatus.Success, await heartbeat.RecordAsync(identity, new AgentHeartbeatReport(1, epoch, 1, null), token));
            Assert.AreEqual(AgentIdentityStatus.Conflict, await heartbeat.BeginSessionAsync(identity, new(null, Guid.CreateVersion7()), token));
            await using var db = await factory.CreateDbContextAsync(token);
            var saved = await db.AgentStatuses.SingleAsync(token);
            Assert.AreEqual(identity.DeviceId, saved.DeviceId);
            Assert.AreEqual(epoch, saved.QueueReportEpoch);
            Assert.AreEqual(2L, saved.QueueReportSequence);
            Assert.IsNotNull(saved.LastHeartbeatAt);
            var monitor = new HostAgentMonitoring(factory, TimeProvider.System);
            var device = new DeviceAgentState(identity.DeviceId, identity.DeviceGeneration, true);
            var registration = new AgentRegistrationSummary(identity.AgentId, identity.DeviceGeneration, DateTimeOffset.UtcNow, null);
            var status = await monitor.GetAsync(device, registration, token);
            Assert.AreEqual(AgentConnectionState.Online, status.State);
            Assert.IsNotNull(status.LastHeartbeatAt);
            Assert.AreEqual(AgentConnectionState.NotRegistered, (await monitor.GetAsync(device, null, token)).State);
            Assert.AreEqual(AgentConnectionState.Disabled, (await monitor.GetAsync(device with { Eligible = false }, registration, token)).State);
            var settings = new HostAgentConfiguration(factory, heartbeat, TimeProvider.System);
            var actor = Guid.CreateVersion7();
            var platform = await settings.UpdateAsync(null, new AgentSettingsUpdate(new AgentSettingsOverrides(120, null, null), ""), actor, token);
            Assert.IsNotNull(platform);
            var deviceSettings = await settings.UpdateAsync(identity.DeviceId, new AgentSettingsUpdate(new AgentSettingsOverrides(90, null, null), ""), actor, token);
            Assert.IsNotNull(deviceSettings);
            Assert.AreEqual(90, (await settings.GetForAgentAsync(identity, token))!.Settings.HeartbeatSeconds);
            Assert.IsNull(await settings.UpdateAsync(null, new AgentSettingsUpdate(new AgentSettingsOverrides(60, null, null), ""), actor, token));
            Assert.AreEqual(2, await db.Set<AgentSettingAudit>().CountAsync(token));
        }, context.CancellationToken);

    [TestMethod]
    public Task InitializesFreshInboxPolicyOnce() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            await HostMonitoringDatabaseCommand.RunAsync(target.ConnectionString, null, token);
            await HostMonitoringDatabaseCommand.RunAsync(target.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(target.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            await using var db = new HostMonitoringDbContext(options);
            var policy = await db.ReplayPolicies.SingleAsync(token);
            Assert.IsTrue(policy.FreshInstallation);
            Assert.IsNotNull(policy.CleanupCutoverAt);
        }, context.CancellationToken);

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
            await using (var resources = new ResourcesDbContext(resourceOptions))
            {
                resources.Agents.Add(new RegisteredAgent { Id = agent.Id, DeviceId = device.Id, DeviceGeneration = device.AgentAuthorizationGeneration, Verifier = new byte[32], CreatedAt = now });
                await resources.SaveChangesAsync(token);
            }
            var resourceFactory = new PooledDbContextFactory<ResourcesDbContext>(resourceOptions);
            var registrations = new Vigiloom.Modules.Resources.Application.AgentIdentity.AgentRegistration(resourceFactory, TimeProvider.System, null!);
            var overview = await registrations.OverviewAsync([device.Id], token);
            Assert.AreEqual(agent.Id, Assert.ContainsSingle(overview).Registration!.Id);
            var epoch = Guid.CreateVersion7();
            agent.RecordHeartbeat(now);
            agent.BeginQueueSession(epoch);
            agent.RecordQueueStatus(2, null, null, now);
            var batchId = Guid.CreateVersion7();
            var messageId = Guid.CreateVersion7();
            var settings = new AgentSetting { DeviceId = device.Id, HeartbeatSeconds = 30, UpdatedBy = Guid.CreateVersion7(), UpdatedAt = now };
            var metricSettings = new AgentMetricSetting { DeviceId = device.Id, HostIntervalSeconds = 60, UpdatedBy = settings.UpdatedBy, UpdatedAt = now };
            var capacity = new AgentQueueCapacitySetting { DeviceId = device.Id, BatchCount = 100, UpdatedBy = settings.UpdatedBy, UpdatedAt = now };
            var logSource = new HostLogSource(device.Id, AgentLogSourceKind.Journal, "System", "local", true, null, settings.UpdatedBy, now);
            var threshold = new HostMetricThreshold { DeviceId = device.Id, AgentId = agent.Id, Kind = AgentMetricKind.CpuTotalPercent,
                Source = "cpu", SeriesHash = new byte[32], Enabled = true, Comparison = HostMetricComparison.AtLeast, Value = 80, FreshnessSeconds = 60 };
            var logMessageId = Guid.CreateVersion7();
            var logBatch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now,
                logSource.Id, AgentLogSourceKind.Journal, "System", 1,
                [new AgentLogRecord(logMessageId, now, "hello", null, null, null, null, null)]);
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
                legacy.Settings.Add(settings);
                legacy.MetricSettings.Add(metricSettings);
                legacy.QueueCapacitySettings.Add(capacity);
                legacy.HostLogSources.Add(logSource);
                legacy.HostMetricThresholds.Add(threshold);
                legacy.HostLogs.Add(new HostLog(agent.Id, logBatch, 0, now));
                await legacy.SaveChangesAsync(token);
                legacy.HostLogSourceAudits.Add(new HostLogSourceAudit(logSource, HostLogSourceAction.Created));
                await legacy.SaveChangesAsync(token);
            }
            await HostMonitoringDatabaseCommand.RunAsync(target.ConnectionString, source.ConnectionString, token);
            await using var copied = new HostMonitoringDbContext(hostOptions);
            var inbox = await copied.InboxStates.SingleAsync(token);
            Assert.AreEqual(agent.Id, inbox.Id);
            Assert.AreEqual(device.Id, inbox.DeviceId);
            Assert.AreEqual(1L, inbox.DeviceGeneration);
            var status = await copied.AgentStatuses.SingleAsync(token);
            Assert.AreEqual(agent.Id, status.Id);
            Assert.AreEqual(epoch, status.QueueReportEpoch);
            Assert.AreEqual(2L, status.QueueReportSequence);
            Assert.AreEqual(now, status.LastHeartbeatAt);
            Assert.AreEqual(1, await copied.ReplayPolicies.CountAsync(token));
            Assert.IsTrue(await copied.ReceivedBatches.AnyAsync(x => x.AgentId == agent.Id && x.BatchId == batchId, token));
            Assert.IsFalse(await copied.ReceivedBatches.AnyAsync(x => EF.Property<bool>(x, "PendingDelivery"), token));
            Assert.IsTrue(await copied.HostMetrics.AnyAsync(x => x.AgentId == agent.Id && x.MessageId == messageId && x.Value == 42, token));
            var copiedRollup = await copied.HostMetricRollups.SingleAsync(token);
            Assert.AreEqual(42d, copiedRollup.Sum);
            Assert.AreEqual(1L, copiedRollup.Count);
            Assert.AreEqual("Windows", (await copied.RuntimeObservations.SingleAsync(token)).OperatingSystem);
            Assert.AreEqual(14, (await copied.HostMetricRetentionSettings.SingleAsync(token)).RawDays);
            var copiedSettings = await copied.Set<AgentSetting>().SingleAsync(token);
            Assert.AreEqual(settings.Id, copiedSettings.Id);
            Assert.AreEqual(30, copiedSettings.HeartbeatSeconds);
            var copiedMetricSettings = await copied.Set<AgentMetricSetting>().SingleAsync(token);
            Assert.AreEqual(metricSettings.Id, copiedMetricSettings.Id);
            Assert.AreEqual(60, copiedMetricSettings.HostIntervalSeconds);
            Assert.AreEqual(100, (await copied.Set<AgentQueueCapacitySetting>().SingleAsync(token)).BatchCount);
            Assert.AreEqual(logSource.Id, (await copied.Set<HostLogSource>().SingleAsync(token)).Id);
            Assert.AreEqual(logSource.Id, (await copied.Set<HostLogSourceAudit>().SingleAsync(token)).SourceId);
            Assert.AreEqual(80d, (await copied.Set<HostMetricThreshold>().SingleAsync(token)).Value);
            Assert.IsTrue(await copied.HostLogMessageIds.AnyAsync(x => x.AgentId == agent.Id && x.MessageId == logMessageId, token));
        }, outerToken), context.CancellationToken);
}

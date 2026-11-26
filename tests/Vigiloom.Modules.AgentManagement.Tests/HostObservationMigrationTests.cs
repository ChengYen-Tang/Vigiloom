using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Application.HostMetrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;
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
    public Task HostInboxPolicyUsesHostDataAndTransactionalAudit() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            await HostMonitoringDatabaseCommand.RunAsync(target.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(target.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            var factory = new PooledDbContextFactory<HostMonitoringDbContext>(options);
            var audit = new TemporalAuditWriter(new OutboxWriter([new TestOutboxHandler()]), new ConfigurationBuilder().Build());
            var settings = new HostInboxSettings(factory, audit, new EphemeralDataProtectionProvider());
            var actor = Guid.CreateVersion7();
            var before = await settings.GetAsync(token);
            Assert.IsTrue(before.CleanupReady);
            var preview = await settings.PreviewAsync(43200, before.RetentionDays, before.Version, actor, token);
            Assert.IsNotNull(preview);
            Assert.IsTrue(await settings.UpdateAsync(43200, before.RetentionDays, before.Version, preview.ConfirmationToken, actor, token));
            var after = await settings.GetAsync(token);
            Assert.AreEqual(43200, after.ReplaySeconds);
            await using var db = await factory.CreateDbContextAsync(token);
            Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM scheduling.Outbox").SingleAsync(token));
            await db.Database.ExecuteSqlRawAsync("DROP TABLE scheduling.Outbox", token);
            await Assert.ThrowsAsync<SqlException>(() => settings.UpdateAsync(86400, after.RetentionDays, after.Version, null, actor, token));
            Assert.AreEqual(after.ReplaySeconds, (await settings.GetAsync(token)).ReplaySeconds);
        }, context.CancellationToken);

    [TestMethod]
    public Task HostRetentionChangesPersistWithAuditDelivery() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (target, token) =>
        {
            await HostMonitoringDatabaseCommand.RunAsync(target.ConnectionString, null, token);
            var options = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(target.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            var factory = new PooledDbContextFactory<HostMonitoringDbContext>(options);
            var audit = new TemporalAuditWriter(new OutboxWriter([new TestOutboxHandler()]), new ConfigurationBuilder().Build());
            var settings = new HostOwnedMetricRetentionSettings(factory, audit, new EphemeralDataProtectionProvider());
            var actor = Guid.CreateVersion7();
            var initial = await settings.GetAsync(token);
            var preview = await settings.PreviewAsync(new(10, 60, initial.Version), actor, token);
            Assert.IsNotNull(preview);
            var saved = await settings.UpdateAsync(new(10, 60, initial.Version, preview.ConfirmationToken), actor, token);
            Assert.IsNotNull(saved);
            Assert.AreEqual(10, saved.RawDays);
            await using var db = await factory.CreateDbContextAsync(token);
            Assert.AreEqual(1, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM scheduling.Outbox").SingleAsync(token));
            await db.Database.ExecuteSqlRawAsync("DROP TABLE scheduling.Outbox", token);
            await Assert.ThrowsAsync<SqlException>(() => settings.UpdateAsync(new(20, 90, saved.Version, null), actor, token));
            Assert.AreEqual(saved, await settings.GetAsync(token));
        }, context.CancellationToken);

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
            Assert.AreEqual(AgentSettingsApplyState.WaitingForReport, (await monitor.GetSettingsStatusAsync(device, registration, token)).State);
            var targetSettings = (await settings.GetForAgentAsync(identity, token))!;
            var report = new AgentConfigurationReport(targetSettings, DateTimeOffset.UtcNow, null);
            Assert.AreEqual(AgentIdentityStatus.Success, await heartbeat.RecordAsync(identity, new AgentHeartbeatReport(1, epoch, 3, null, report), token));
            Assert.AreEqual(AgentSettingsApplyState.Applied, (await monitor.GetSettingsStatusAsync(device, registration, token)).State);
            var changed = await settings.UpdateAsync(identity.DeviceId, new AgentSettingsUpdate(new AgentSettingsOverrides(75, null, null), deviceSettings.Version), actor, token);
            Assert.IsNotNull(changed);
            Assert.AreEqual(AgentSettingsApplyState.Pending, (await monitor.GetSettingsStatusAsync(device, registration, token)).State);
            Assert.AreEqual(AgentSettingsApplyState.NotRegistered, (await monitor.GetSettingsStatusAsync(device, null, token)).State);
            Assert.AreEqual(AgentSettingsApplyState.WaitingForReport, (await monitor.GetQueueCapacityStatusAsync(device, registration, token)).State);
            var capacity = AgentQueueCapacitySnapshot.Create(AgentQueueCapacity.Default);
            var queue = new AgentQueueSnapshot(0, 0, 0, capacity.Capacity.PayloadBytes, capacity.Capacity.BatchCount);
            var capacityReport = new AgentQueueCapacityReport(capacity, DateTimeOffset.UtcNow, null);
            Assert.AreEqual(AgentIdentityStatus.Success, await heartbeat.RecordAsync(identity, new AgentHeartbeatReport(1, epoch, 4, queue, CapacityConfiguration: capacityReport), token));
            Assert.AreEqual(AgentSettingsApplyState.Applied, (await monitor.GetQueueCapacityStatusAsync(device, registration, token)).State);
            var queueSettings = new HostQueueCapacity(factory, TimeProvider.System);
            var queueUpdate = new AgentQueueCapacityUpdate(new AgentQueueCapacityOverrides(128L * 1024 * 1024, null), "");
            Assert.IsNotNull(await queueSettings.UpdateAsync(identity.DeviceId, queueUpdate, actor, token));
            Assert.AreEqual(AgentSettingsApplyState.Pending, (await monitor.GetQueueCapacityStatusAsync(device, registration, token)).State);
            var newCapacity = await queueSettings.GetForAgentAsync(identity, token);
            Assert.AreEqual(128L * 1024 * 1024, newCapacity.Capacity.PayloadBytes);
            var newQueue = new AgentQueueSnapshot(0, 0, 0, newCapacity.Capacity.PayloadBytes, newCapacity.Capacity.BatchCount);
            var newReport = new AgentQueueCapacityReport(newCapacity, DateTimeOffset.UtcNow, null);
            Assert.AreEqual(AgentIdentityStatus.Success, await heartbeat.RecordAsync(identity, new AgentHeartbeatReport(1, epoch, 5, newQueue, CapacityConfiguration: newReport), token));
            Assert.AreEqual(AgentSettingsApplyState.Applied, (await monitor.GetQueueCapacityStatusAsync(device, registration, token)).State);
            Assert.AreEqual(1, await db.Set<AgentQueueCapacityAudit>().CountAsync(token));
            Assert.AreEqual(AgentSettingsApplyState.NotRegistered, (await monitor.GetQueueCapacityStatusAsync(device, null, token)).State);
            Assert.AreEqual(AgentSettingsApplyState.Disabled, (await monitor.GetQueueCapacityStatusAsync(device with { Eligible = false }, registration, token)).State);
            var targetUpdate = new RecordedAgentUpdateTarget(Guid.CreateVersion7(), identity.AgentId, Guid.CreateVersion7(), "1.2.3", "win-x64", new string('a', 64), DateTimeOffset.UtcNow);
            var updateResults = new HostUpdateResults(factory, new TargetReader(targetUpdate), TimeProvider.System);
            var updateReport = new AgentUpdateResult(Guid.CreateVersion7(), targetUpdate.UpdateId, targetUpdate.PackageId, targetUpdate.ManifestSha256, 1,
                AgentUpdateResultKind.Succeeded, DateTimeOffset.UtcNow, false, null);
            Assert.AreEqual(AgentUpdateResultStatus.Success, await updateResults.ReceiveAsync(identity, updateReport, token));
            Assert.AreEqual(AgentUpdateResultStatus.Success, await updateResults.ReceiveAsync(identity, updateReport, token));
            Assert.AreEqual(AgentUpdateResultStatus.Conflict, await updateResults.ReceiveAsync(identity, updateReport with { Sequence = 2 }, token));
            Assert.AreEqual("1.2.3", (await updateResults.GetAsync(identity.AgentId, token))!.LatestResult!.PackageVersion);
            var newIdentity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var newTarget = targetUpdate with { AgentId = newIdentity.AgentId, UpdateId = Guid.CreateVersion7() };
            var newResults = new HostUpdateResults(factory, new TargetReader(newTarget), TimeProvider.System);
            var newUpdateReport = updateReport with { MessageId = Guid.CreateVersion7(), UpdateId = newTarget.UpdateId };
            Assert.AreEqual(AgentUpdateResultStatus.Success, await newResults.ReceiveAsync(newIdentity, newUpdateReport, token));
            Assert.AreEqual("1.2.3", (await newResults.GetAsync(newIdentity.AgentId, token))!.LatestResult!.PackageVersion);
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
            Assert.IsFalse(db.Database.HasPendingModelChanges());
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
            var ownership = await registrations.GetOwnershipAsync(device.Id, token);
            Assert.AreEqual(agent.Id, ownership!.Registration!.Id);
            Assert.AreEqual(device.AgentAuthorizationGeneration, ownership.Device.Generation);
            Assert.IsNull(await registrations.GetOwnershipAsync(Guid.CreateVersion7(), token));
            CollectionAssert.AreEqual(new[] { agent.Id }, await registrations.ActiveAsync([agent.Id, Guid.CreateVersion7()], token));
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
            var package = new Vigiloom.Modules.AgentManagement.Domain.AgentPackage { Id = Guid.CreateVersion7(), Version = "1.2.3", RuntimeIdentifier = "win-x64", Sha256 = new string('a', 64), ManifestSha256 = new string('b', 64), Size = 10 };
            var updateTarget = new Vigiloom.Modules.AgentManagement.Domain.AgentUpdateTarget { Id = Guid.CreateVersion7(), AgentId = agent.Id, PackageId = package.Id,
                Version = package.Version, RuntimeIdentifier = package.RuntimeIdentifier, ManifestSha256 = package.ManifestSha256, AssignedAt = now };
            var updateMessage = Guid.CreateVersion7();
            var logBatch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now,
                logSource.Id, AgentLogSourceKind.Journal, "System", 1,
                [new AgentLogRecord(logMessageId, now, "hello", null, null, null, null, null)]);
            await using (var legacy = new AgentManagementDbContext(legacyOptions))
            {
                legacy.Agents.Add(agent);
                legacy.Set<Vigiloom.Modules.AgentManagement.Domain.AgentPackage>().Add(package);
                legacy.UpdateTargets.Add(updateTarget);
                legacy.UpdateReceipts.Add(new AgentUpdateReceipt { AgentId = agent.Id, MessageId = updateMessage, UpdateId = updateTarget.Id,
                    Sequence = 1, Result = AgentUpdateResultKind.Succeeded, OccurredAt = now, ReceivedAt = now });
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
            Assert.IsTrue(await copied.UpdateReceipts.AnyAsync(x => x.AgentId == agent.Id && x.MessageId == updateMessage, token));
            var status = await copied.AgentStatuses.SingleAsync(token);
            Assert.AreEqual(agent.Id, status.Id);
            Assert.AreEqual(epoch, status.QueueReportEpoch);
            Assert.AreEqual(2L, status.QueueReportSequence);
            Assert.AreEqual(now, status.LastHeartbeatAt);
            Assert.AreEqual(1, await copied.ReplayPolicies.CountAsync(token));
            Assert.IsTrue(await copied.ReceivedBatches.AnyAsync(x => x.AgentId == agent.Id && x.BatchId == batchId, token));
            Assert.IsFalse(await copied.ReceivedBatches.AnyAsync(x => EF.Property<bool>(x, "PendingDelivery"), token));
            Assert.IsTrue(await copied.HostMetrics.AnyAsync(x => x.AgentId == agent.Id && x.MessageId == messageId && x.Value == 42, token));
            var metricReader = new HostMetricReader(new PooledDbContextFactory<HostMonitoringDbContext>(hostOptions));
            var metricPage = await metricReader.GetAsync(device.Id, now.AddMinutes(-1), now.AddMinutes(1), null, AgentMetricKind.CpuTotalPercent, token);
            Assert.AreEqual(42d, Assert.ContainsSingle(metricPage!.Items).Value);
            var series = await metricReader.GetSeriesAsync(device.Id, now.AddMinutes(-1), now.AddMinutes(1), AgentMetricKind.CpuTotalPercent, 0, token);
            Assert.IsTrue(series!.Items.Any(x => x.AgentId == agent.Id && x.Source == "cpu"));
            var aggregate = await metricReader.AggregateAsync(device.Id, agent.Id, now.AddMinutes(-1), now.AddMinutes(1), AgentMetricKind.CpuTotalPercent, "cpu", null, 300, token);
            Assert.IsNotNull(aggregate);
            Assert.IsTrue(aggregate.Buckets.Any(x => x.Average == 42d));
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
            var oldMessageId = Guid.CreateVersion7();
            copied.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(oldMessageId, now.AddDays(-20), AgentMetricKind.CpuTotalPercent, "cpu", null, 15), now.AddDays(-20)));
            await copied.SaveChangesAsync(token);
            var maintenance = new HostMetricMaintenance(new PooledDbContextFactory<HostMonitoringDbContext>(hostOptions), NullLogger<HostMetricMaintenance>.Instance);
            await maintenance.RollupAsync(token);
            await maintenance.RetainAsync(token);
            Assert.IsFalse(await copied.HostMetrics.AnyAsync(x => x.MessageId == oldMessageId, token));
            Assert.IsTrue(await copied.HostMetrics.AnyAsync(x => x.MessageId == messageId && x.Aggregated, token));
            Assert.IsTrue(await copied.HostMetricRollups.AnyAsync(x => x.AgentId == agent.Id && x.Sum == 15d, token));
        }, outerToken), context.CancellationToken);

    private sealed class TargetReader(RecordedAgentUpdateTarget target) : IRecordedAgentTargetReader
    {
        public Task<RecordedAgentUpdateTarget?> GetAsync(Guid agentId, Guid updateId, CancellationToken token) =>
            Task.FromResult<RecordedAgentUpdateTarget?>(target.AgentId == agentId && target.UpdateId == updateId ? target : null);
    }

    private sealed class TestOutboxHandler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken token) =>
            throw new NotSupportedException();
    }
}

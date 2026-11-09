using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.SqlClient;
using System.Data.Common;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.Resources.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.AgentManagement.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentIdentitySqlTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task QueueCapacity_InheritsOverridesAndAuditsAtomically(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("capacity server", "127.0.0.9", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var source = new DeviceAgentSource(resources);
        var identity = new AgentIdentity(agents, source, clock);
        var settings = new AgentQueueCapacityConfiguration(agents, source, identity, clock);
        var actor = Guid.CreateVersion7();
        var platform = await settings.GetAsync(null, token);
        Assert.IsNotNull(platform);
        Assert.AreEqual(AgentQueueCapacity.Default, platform.Effective.Capacity);
        var edited = await settings.UpdateAsync(null, new(new(128L * 1024 * 1024, 5000), platform.Version), actor, token);
        Assert.IsNotNull(edited);
        var inherited = await settings.GetAsync(device.Id, token);
        Assert.AreEqual(edited.Effective, inherited!.Effective);
        var custom = await settings.UpdateAsync(device.Id, new(new(null, 100), inherited.Version), actor, token);
        Assert.IsNotNull(custom);
        Assert.AreEqual(128L * 1024 * 1024, custom.Effective.Capacity.PayloadBytes);
        Assert.AreEqual(100, custom.Effective.Capacity.BatchCount);
        Assert.IsNull(await settings.UpdateAsync(device.Id, new(new(null, 200), inherited.Version), actor, token));
        var reset = await settings.UpdateAsync(device.Id, new(new(null, null), custom.Version), actor, token);
        Assert.IsNotNull(reset);
        Assert.AreEqual(edited.Effective, reset.Effective);
        var issued = await identity.IssueAsync(device.Id, actor, token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        Assert.AreEqual(reset.Effective, await settings.GetForAgentAsync(credential, token));
        Assert.IsNull(await settings.GetForAgentAsync("invalid", token));
        var page = await new AgentAuditFeed(agents).QueryAsync(AuditFeedSource.AgentQueueCapacity,
            new OperationAuditQuery(clock.Now.AddDays(-1), clock.Now.AddDays(1)), token);
        Assert.HasCount(3, page.Items);
        var detail = page.Items.First(x => x.SubjectId == device.Id && x.QueueCapacity!.BatchCount == 100);
        Assert.AreEqual(AuditFeedAction.AgentQueueCapacityChanged, detail.Action);
        Assert.AreEqual(128L * 1024 * 1024, detail.QueueCapacity!.EffectivePayloadBytes);
        Assert.AreEqual(100, detail.QueueCapacity.EffectiveBatchCount);
        await using (var invalid = agents.CreateDbContext())
        {
            invalid.QueueCapacitySettings.Add(new AgentQueueCapacitySetting { DeviceId = Guid.CreateVersion7(), UpdatedAt = clock.Now, UpdatedBy = actor });
            await Assert.ThrowsAsync<DbUpdateException>(() => invalid.SaveChangesAsync(token));
        }
        await using (var invalid = agents.CreateDbContext())
        {
            var audit = await invalid.QueueCapacityAudits.FirstAsync(token);
            audit.DeviceId = Guid.CreateVersion7();
            await Assert.ThrowsAsync<DbUpdateException>(() => invalid.SaveChangesAsync(token));
        }
        await using (var db = agents.CreateDbContext())
        {
            Assert.AreEqual(3, await db.QueueCapacityAudits.CountAsync(token));
            await db.Database.ExecuteSqlRawAsync("DROP TABLE agent.QueueCapacityAudits", token);
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => settings.UpdateAsync(device.Id, new(new(null, 200), reset.Version), actor, token));
        Assert.AreEqual(reset, await settings.GetAsync(device.Id, token), "A failed audit must roll back the setting change.");
    }, snapshotReads);
    public TestContext TestContext { get; set; } = null!;
    [AssemblyCleanup] public static void Cleanup() => SqlTestDatabase.CompleteRun();

    [TestMethod]
    public Task QueueCapacityStatus_DoesNotExposeOutdatedReportsDuringRollingUpgrade() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("capacity status", "127.0.0.10", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var source = new DeviceAgentSource(resources);
        var identity = new AgentIdentity(agents, source, clock);
        var monitor = new AgentQueueCapacityMonitoring(agents, source, clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        var epoch = Guid.CreateVersion7();
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.BeginQueueSessionAsync(credential, new(null, epoch), token));
        var applied = AgentQueueCapacitySnapshot.Create(AgentQueueCapacity.Default);
        var queue = new AgentQueueSnapshot(0, 0, 0, applied.Capacity.PayloadBytes, applied.Capacity.BatchCount);
        var report = new AgentQueueCapacityReport(applied, clock.Now, null);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new(1, epoch, 1, queue, CapacityConfiguration: report), token));
        Assert.AreEqual(AgentSettingsApplyState.Applied, (await monitor.GetAsync(device.Id, token))!.State);
        await using (var legacy = agents.CreateDbContext())
            await legacy.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent.Agents SET QueueReportSequence = {2L} WHERE Id = {registration.AgentId!.Value}", token);
        Assert.IsNull((await monitor.GetAsync(device.Id, token))!.Applied, "A legacy node's newer queue report must hide the older capacity observation.");
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new(1, epoch, 3, queue, CapacityConfiguration: report), token));
        Assert.AreEqual(AgentSettingsApplyState.Applied, (await monitor.GetAsync(device.Id, token))!.State);
        await using (var legacy = agents.CreateDbContext())
            await legacy.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent.Agents SET QueueReportEpoch = {Guid.CreateVersion7()} WHERE Id = {registration.AgentId!.Value}", token);
        Assert.IsNull((await monitor.GetAsync(device.Id, token))!.Applied, "A legacy node's replacement session must hide the older capacity observation.");
    });

    [TestMethod]
    public Task Configuration_ReportsPendingAppliedFailureAndOfflineWithoutAcceptingStaleStatus() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("settings status server", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var source = new DeviceAgentSource(resources);
        var identity = new AgentIdentity(agents, source, clock);
        var configuration = new AgentConfiguration(agents, source, identity, clock);
        var monitoring = new AgentSettingsMonitoring(agents, source, clock);
        Assert.AreEqual(AgentSettingsApplyState.NotRegistered, (await monitoring.GetAsync(device.Id, token))!.State);
        var actor = Guid.CreateVersion7();
        var issued = await identity.IssueAsync(device.Id, actor, token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        var identityFeed = await new AgentAuditFeed(agents).QueryAsync(AuditFeedSource.AgentIdentity,
            new OperationAuditQuery(clock.Now.AddDays(-1), clock.Now.AddDays(1)), token);
        Assert.IsTrue(identityFeed.Items.Any(item => item.Action == AuditFeedAction.AgentEnrolled &&
            item.SubjectId == device.Id && item.AgentId == registration.AgentId && item.ActorId is null));
        Assert.AreEqual(AgentSettingsApplyState.WaitingForHeartbeat, (await monitoring.GetAsync(device.Id, token))!.State);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, token));
        Assert.AreEqual(AgentSettingsApplyState.WaitingForReport, (await monitoring.GetAsync(device.Id, token))!.State);
        var epoch = Guid.CreateVersion7();
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.BeginQueueSessionAsync(credential, new AgentQueueSessionRequest(null, epoch), token));
        var initial = AgentSettingsSnapshot.Create(AgentSettings.Default);
        var applied = new AgentConfigurationReport(initial, clock.Now, null);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 2, null, applied), token));
        Assert.AreEqual(AgentSettingsApplyState.Applied, (await monitoring.GetAsync(device.Id, token))!.State);
        await configuration.UpdateAsync(device.Id, new AgentSettingsUpdate(new(60, null, null), ""), actor, token);
        Assert.AreEqual(AgentSettingsApplyState.Pending, (await monitoring.GetAsync(device.Id, token))!.State);
        var failed = applied with { Failure = AgentConfigurationFailure.Invalid };
        clock.Now = clock.Now.AddSeconds(1);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 3, null, failed), token));
        var status = (await monitoring.GetAsync(device.Id, token))!;
        Assert.AreEqual(AgentSettingsApplyState.Failed, status.State);
        Assert.AreEqual(AgentConfigurationFailure.Invalid, status.Failure);
        var reportedAt = status.ReportedAt;
        clock.Now = clock.Now.AddSeconds(1);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 1, null, applied), token));
        status = (await monitoring.GetAsync(device.Id, token))!;
        Assert.AreEqual(AgentSettingsApplyState.Failed, status.State);
        Assert.AreEqual(reportedAt, status.ReportedAt, "An older report must not refresh or replace configuration status.");
        clock.Now = clock.Now.AddSeconds(89);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, token));
        Assert.AreEqual(AgentSettingsApplyState.ReportStale, (await monitoring.GetAsync(device.Id, token))!.State,
            "An empty heartbeat must not make an old settings report appear current.");
        clock.Now = clock.Now.AddSeconds(90);
        Assert.AreEqual(AgentSettingsApplyState.Offline, (await monitoring.GetAsync(device.Id, token))!.State);
    });

    [TestMethod]
    public Task Configuration_SqlEnforcesResourceKeysAndAuditReferences() => WithDatabase(async (resources, agents, token) =>
    {
        var actor = Guid.CreateVersion7();
        var missingDevice = Guid.CreateVersion7();
        await using (var db = agents.CreateDbContext())
        {
            db.Settings.Add(new AgentSetting { DeviceId = missingDevice, UpdatedBy = actor, UpdatedAt = DateTimeOffset.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
        }
        await using (var db = agents.CreateDbContext())
        {
            db.Settings.Add(new AgentSetting { UpdatedBy = actor, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(token);
        }
        await using (var db = agents.CreateDbContext())
        {
            db.Settings.Add(new AgentSetting { UpdatedBy = actor, UpdatedAt = DateTimeOffset.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
        }
        await using (var db = agents.CreateDbContext())
        {
            db.SettingAudits.Add(new AgentSettingAudit
            {
                SettingId = Guid.CreateVersion7(), ActorId = actor, OccurredAt = DateTimeOffset.UtcNow,
                EffectiveRevision = new string('a', 64), ChangeVersion = new byte[8]
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(token));
        }
        await using var verify = agents.CreateDbContext();
        var setting = await verify.Settings.SingleAsync(token);
        Assert.AreEqual(7, setting.Id.Version);
        Assert.IsGreaterThan(0, setting.RowId);
        Assert.HasCount(8, setting.Version);
    });

    [TestMethod]
    public Task Configuration_AuditFailureRollsBackTheSetting() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var source = new DeviceAgentSource(resources);
        var config = new AgentConfiguration(agents, source, new AgentIdentity(agents, source, clock), clock);
        var actor = Guid.CreateVersion7();
        var saved = (await config.UpdateAsync(null, new AgentSettingsUpdate(new(60, null, null), ""), actor, token))!;
        var rejectedActor = Guid.Parse("018f0000-0000-7000-8000-000000000001");
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [agent].[SettingAudits] ADD CONSTRAINT [CK_TestActor] CHECK ([ActorId] <> '018f0000-0000-7000-8000-000000000001')", token);
        await Assert.ThrowsAsync<DbUpdateException>(() => config.UpdateAsync(null,
            new AgentSettingsUpdate(new(120, null, null), saved.Version), rejectedActor, token));
        Assert.AreEqual(saved, await config.GetAsync(null, token));
        await using var verify = agents.CreateDbContext();
        Assert.AreEqual(1, await verify.SettingAudits.CountAsync(token));
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Configuration_ResolvesLayersAndAuditsConcurrentEdits(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("configuration server", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var source = new DeviceAgentSource(resources);
        var identity = new AgentIdentity(agents, source, clock);
        var config = new AgentConfiguration(agents, source, identity, clock);
        var actor = Guid.CreateVersion7();
        var initial = (await config.GetAsync(device.Id, token))!;
        Assert.AreEqual(AgentSettings.Default, initial.Effective.Settings);
        Assert.AreEqual("", initial.Version);
        var platform = (await config.UpdateAsync(null, new AgentSettingsUpdate(new(60, null, null), ""), actor, token))!;
        Assert.AreEqual(60, platform.Effective.Settings.HeartbeatSeconds);
        var specific = (await config.UpdateAsync(device.Id, new AgentSettingsUpdate(new(null, null, 10), ""), actor, token))!;
        Assert.AreEqual(new AgentSettings(60, 300, 10), specific.Effective.Settings);
        Assert.AreEqual(platform.Effective.Settings.HeartbeatSeconds, (await config.GetAsync(device.Id, token))!.Effective.Settings.HeartbeatSeconds);
        Assert.IsNull(await config.UpdateAsync(device.Id, new AgentSettingsUpdate(new(90, null, null), ""), actor, token));
        await Assert.ThrowsAsync<ArgumentException>(() => config.UpdateAsync(device.Id, new AgentSettingsUpdate(new(1, null, null), specific.Version), actor, token));
        var candidates = await Task.WhenAll(
            config.UpdateAsync(device.Id, new AgentSettingsUpdate(new(90, null, 10), specific.Version), actor, token),
            config.UpdateAsync(device.Id, new AgentSettingsUpdate(new(120, null, 10), specific.Version), actor, token));
        Assert.AreEqual(1, candidates.Count(x => x is not null));
        var winner = candidates.Single(x => x is not null)!;
        var reset = (await config.UpdateAsync(device.Id, new AgentSettingsUpdate(new(null, null, null), winner.Version), actor, token))!;
        Assert.AreEqual(platform.Effective.Settings, reset.Effective.Settings);
        await using var verify = agents.CreateDbContext();
        Assert.AreEqual(2, await verify.Settings.CountAsync(token));
        var audits = await verify.SettingAudits.OrderBy(x => x.RowId).ToListAsync(token);
        Assert.HasCount(4, audits);
        Assert.IsTrue(audits.All(x => x.ActorId == actor && x.ChangeVersion.Length == 8));
        Assert.AreEqual(reset.Effective.Revision, audits[^1].EffectiveRevision);
        Assert.AreEqual(reset.Effective.Settings.HeartbeatSeconds, audits[^1].EffectiveHeartbeatSeconds);
        Assert.AreEqual(reset.Version, Convert.ToBase64String(audits[^1].ChangeVersion));
        var feed = new AgentAuditFeed(agents);
        var range = new OperationAuditQuery(clock.Now.AddDays(-1), clock.Now.AddDays(1));
        var records = await feed.QueryAsync(AuditFeedSource.AgentSettings, range, token);
        Assert.HasCount(4, records.Items);
        var last = records.Items[0];
        Assert.AreEqual(AuditFeedAction.AgentSettingsChanged, last.Action);
        Assert.AreEqual(device.Id, last.SubjectId);
        Assert.IsNotNull(last.AgentSettings);
        Assert.AreEqual(reset.Effective.Settings.HeartbeatSeconds, last.AgentSettings.EffectiveHeartbeatSeconds);
        Assert.AreEqual(reset.Version, last.AgentSettings.Version);
        audits[^1].HeartbeatSeconds = 10; // Contradicts the stored effective value despite a valid revision.
        await verify.SaveChangesAsync(token);
        var corrupted = (await feed.QueryAsync(AuditFeedSource.AgentSettings, range, token)).Items[0];
        Assert.AreEqual(AuditFeedAction.Unknown, corrupted.Action);
        Assert.IsNull(corrupted.AgentSettings);
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Configuration_ConcurrentRevocationPreventsDelivery(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("revoked configuration", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var source = new DeviceAgentSource(resources);
        var identity = new AgentIdentity(agents, source, clock);
        var config = new AgentConfiguration(agents, source, identity, clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        await using var revoke = agents.CreateDbContext();
        await using var transaction = await revoke.Database.BeginTransactionAsync(token);
        await using (var command = revoke.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = $"vigiloom.agent.device.{device.Id:D}"; command.Parameters.Add(parameter);
            Assert.IsGreaterThanOrEqualTo(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
        }
        (await revoke.Agents.SingleAsync(x => x.Id == registration.AgentId, token)).Revoke(clock.Now);
        await revoke.SaveChangesAsync(token);
        var waiting = config.GetForAgentAsync(credential, token);
        await Task.Delay(250, token);
        Assert.IsFalse(waiting.IsCompleted, "Configuration delivery must wait for the revocation transaction.");
        await transaction.CommitAsync(token);
        Assert.IsNull(await waiting);
    }, snapshotReads);

    [TestMethod]
    public Task Configuration_AgentReadsOnlyItsCurrentDeviceAndRevocationTakesEffect() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("configured machine", "127.0.0.1", true, clock.Now);
        var otherDevice = new Device("other configured machine", "127.0.0.2", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.AddRange(device, otherDevice); await db.SaveChangesAsync(token); }
        var source = new DeviceAgentSource(resources);
        var identity = new AgentIdentity(agents, source, clock);
        var config = new AgentConfiguration(agents, source, identity, clock);
        var actor = Guid.CreateVersion7();
        var issued = await identity.IssueAsync(device.Id, actor, token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registered.AgentId!.Value, secret);
        var otherIssued = await identity.IssueAsync(otherDevice.Id, actor, token);
        var otherSecret = RandomNumberGenerator.GetBytes(32);
        var otherRegistration = await identity.RedeemAsync(otherIssued.Secret, SHA256.HashData(otherSecret), token);
        var otherCredential = Credential(otherRegistration.AgentId!.Value, otherSecret);
        var saved = (await config.UpdateAsync(device.Id, new AgentSettingsUpdate(new(90, null, null), ""), actor, token))!;
        var otherSaved = (await config.UpdateAsync(otherDevice.Id, new AgentSettingsUpdate(new(120, null, null), ""), actor, token))!;
        var monitoring = new AgentMonitoring(agents, source, clock);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, token));
        Assert.AreEqual(90, (await monitoring.GetAsync(device.Id, token))!.OfflineAfterSeconds);
        Assert.AreEqual(saved.Effective, await config.GetForAgentAsync(credential, token));
        Assert.AreEqual(otherSaved.Effective, await config.GetForAgentAsync(otherCredential, token));
        Assert.AreNotEqual(saved.Effective, otherSaved.Effective);
        Assert.AreEqual(270, (await monitoring.GetAsync(device.Id, token))!.OfflineAfterSeconds, "The threshold must widen before the longer interval is delivered.");
        var reduced = (await config.UpdateAsync(device.Id, new AgentSettingsUpdate(new(30, null, null), saved.Version), actor, token))!;
        Assert.AreEqual(reduced.Effective, await config.GetForAgentAsync(credential, token));
        clock.Now = clock.Now.AddSeconds(100);
        Assert.AreEqual(AgentConnectionState.Online, (await monitoring.GetAsync(device.Id, token))!.State, "A lost longer-interval report must not cause a false offline state after a later reduction.");
        Assert.AreEqual(270, (await monitoring.GetAsync(device.Id, token))!.OfflineAfterSeconds);
        Assert.IsNull(await config.GetForAgentAsync("invalid", token));
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RevokeAsync(registered.AgentId.Value, actor, token));
        Assert.IsNull(await config.GetForAgentAsync(credential, token));
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Batches_AreAtomicIdempotentAndRejectChangedMessages(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("batch server", "127.0.0.1", true, clock.Now);
        var otherDevice = new Device("other batch server", "127.0.0.2", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.AddRange(device, otherDevice); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var otherIssued = await identity.IssueAsync(otherDevice.Id, Guid.CreateVersion7(), token);
        await identity.RedeemAsync(otherIssued.Secret, SHA256.HashData(RandomNumberGenerator.GetBytes(32)), token);
        var credential = Credential(registered.AgentId!.Value, secret);
        var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "Windows test", "X64", ".NET 10");
        var other = item with { MessageId = Guid.CreateVersion7(), OperatingSystem = "Linux test" };
        var batch = new AgentBatch(1, Guid.CreateVersion7(), clock.Now, [item, other]);
        var payload = AgentBatchCodec.Serialize(batch);
        var receiver = new AgentBatchReceiver(agents, identity, clock);
        var receiverHealth = new AgentReceiverMonitoring(agents);
        var monitoring = new AgentMonitoring(agents, new DeviceAgentSource(resources), clock);
        Assert.IsNull(await receiverHealth.LastCommittedBatchAsync(token));
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.LastCommittedBatchAt);
        Assert.AreEqual(new AgentReceiptCapacity(0, 0, AgentBatchReceiver.MaximumRetainedBatchesPerAgent, AgentBatchReceiver.MaximumRetainedBytesPerAgent),
            (await monitoring.GetAsync(device.Id, token))!.ReceiptCapacity);
        var results = await Task.WhenAll(receiver.ReceiveAsync(credential, payload, token), receiver.ReceiveAsync(credential, payload, token));
        Assert.AreEqual(1, results.Count(x => x == BatchReceiptStatus.Accepted));
        Assert.AreEqual(1, results.Count(x => x == BatchReceiptStatus.AlreadyAccepted));
        Assert.AreEqual(clock.Now, await receiverHealth.LastCommittedBatchAsync(token));
        Assert.AreEqual(clock.Now, (await monitoring.GetAsync(device.Id, token))!.LastCommittedBatchAt);
        Assert.IsNull((await monitoring.GetAsync(otherDevice.Id, token))!.LastCommittedBatchAt);
        Assert.AreEqual(new AgentReceiptCapacity(1, payload.Length, AgentBatchReceiver.MaximumRetainedBatchesPerAgent, AgentBatchReceiver.MaximumRetainedBytesPerAgent),
            (await monitoring.GetAsync(device.Id, token))!.ReceiptCapacity);
        Assert.AreEqual(0L, (await monitoring.GetAsync(otherDevice.Id, token))!.ReceiptCapacity!.RetainedBatches);
        clock.Now = clock.Now.AddMinutes(1);
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, payload, token));
        Assert.AreEqual(clock.Now.AddMinutes(-1), await receiverHealth.LastCommittedBatchAsync(token));
        Assert.AreEqual(clock.Now.AddMinutes(-1), (await monitoring.GetAsync(device.Id, token))!.LastCommittedBatchAt);
        Assert.AreEqual(1L, (await monitoring.GetAsync(device.Id, token))!.ReceiptCapacity!.RetainedBatches);
        Assert.AreEqual(BatchReceiptStatus.Conflict, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(batch with { Items = [item with { RuntimeVersion = "different" }, other] }), token));
        var anotherBatch = batch with { BatchId = Guid.CreateVersion7(), Items = [item] };
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(anotherBatch), token));
        Assert.AreEqual(clock.Now, await receiverHealth.LastCommittedBatchAsync(token));
        Assert.AreEqual(clock.Now, (await monitoring.GetAsync(device.Id, token))!.LastCommittedBatchAt);
        var newMessage = other with { MessageId = Guid.CreateVersion7() };
        var conflictingMessage = anotherBatch with { BatchId = Guid.CreateVersion7(), Items = [newMessage, item with { RuntimeVersion = "different" }] };
        Assert.AreEqual(BatchReceiptStatus.Conflict, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(conflictingMessage), token));
        Assert.AreEqual(BatchReceiptStatus.Invalid, await receiver.ReceiveAsync(credential, "{"u8.ToArray(), token));
        Assert.AreEqual(BatchReceiptStatus.Unauthorized, await receiver.ReceiveAsync("invalid", payload, token));
        await using var verify = agents.CreateDbContext();
        Assert.AreEqual(2, await verify.ReceivedBatches.CountAsync(token));
        var observations = await verify.RuntimeObservations.OrderBy(x => x.Ordinal).ToArrayAsync(token);
        Assert.HasCount(2, observations);
        Assert.AreEqual(item.MessageId, observations[0].MessageId);
        Assert.AreEqual(other.MessageId, observations[1].MessageId);
        Assert.AreEqual(".NET 10", observations[0].RuntimeVersion);
        var administrator = Guid.CreateVersion7();
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RevokeAsync(registered.AgentId.Value, administrator, token));
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.LastCommittedBatchAt);
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.ReceiptCapacity);
        var replacement = await identity.IssueAsync(device.Id, administrator, token);
        await identity.RedeemAsync(replacement.Secret, SHA256.HashData(RandomNumberGenerator.GetBytes(32)), token);
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.LastCommittedBatchAt, "A new Agent must not inherit the old identity's receipt history.");
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Batches_ClockRollbackPreservesAcknowledgmentsAndPausesNewReceipts(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("clock server", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var agentId = registered.AgentId!.Value;
        var credential = Credential(agentId, secret);
        var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "Windows test", "X64", ".NET 10");
        var batch = new AgentBatch(1, Guid.CreateVersion7(), clock.Now, [item]);
        var bytes = AgentBatchCodec.Serialize(batch);
        var receiver = new AgentBatchReceiver(agents, identity, clock);
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [agent].[ReceivedBatches] ADD CONSTRAINT [CK_TestReceiptFailure] CHECK ([ByteCount] = -1)", token);
        await Assert.ThrowsAsync<DbUpdateException>(() => receiver.ReceiveAsync(credential, bytes, token));
        await using (var db = agents.CreateDbContext())
        {
            Assert.IsNull((await db.Agents.SingleAsync(x => x.Id == agentId, token)).ReplayClockFloor);
            Assert.AreEqual(0, await db.ReceivedBatches.CountAsync(token));
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [agent].[ReceivedBatches] DROP CONSTRAINT [CK_TestReceiptFailure]", token);
        }
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, bytes, token));
        await using (var db = agents.CreateDbContext())
        {
            Assert.IsNotNull((await db.Agents.SingleAsync(x => x.Id == agentId, token)).ReplayClockFloor);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[Agents] SET [ReplayClockFloor] = DATEADD(day, 1, SYSDATETIMEOFFSET()) WHERE [Id] = {agentId}", token);
        }
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, bytes, token));
        Assert.AreEqual(BatchReceiptStatus.Conflict, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(batch with { Items = [item with { RuntimeVersion = "different" }] }), token));
        var next = AgentBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7() });
        Assert.AreEqual(BatchReceiptStatus.ClockUncertain, await receiver.ReceiveAsync(credential, next, token));
        await using (var db = agents.CreateDbContext())
        {
            Assert.AreEqual(1, await db.ReceivedBatches.CountAsync(token));
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[Agents] SET [ReplayClockFloor] = DATEADD(day, -1, SYSDATETIMEOFFSET()) WHERE [Id] = {agentId}", token);
        }
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, next, token));
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ReplayPolicy_TransitionsAreAuditedOnceAndGraceCannotRestart(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE [agent].[ReplayPolicyTestAudit] ([Id] int IDENTITY PRIMARY KEY)", token);
        var writer = new ReplayPolicyTestAuditWriter();
        var service = new AgentReplayPolicyService(agents, writer);
        var initial = await service.GetAsync(token);
        Assert.AreEqual(AgentReplayPhase.Disabled, initial.Phase);
        writer.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransitionAsync(AgentReplayPhase.Grace, initial.Version, Guid.CreateVersion7(), token));
        Assert.AreEqual(initial, await service.GetAsync(token));
        writer.Fail = false;
        var attempts = await Task.WhenAll(
            service.TransitionAsync(AgentReplayPhase.Grace, initial.Version, Guid.CreateVersion7(), token),
            service.TransitionAsync(AgentReplayPhase.Grace, initial.Version, Guid.CreateVersion7(), token));
        Assert.AreEqual(1, attempts.Count(x => x is not null));
        var grace = attempts.Single(x => x is not null)!;
        Assert.AreEqual(AgentReplayPhase.Grace, grace.Phase);
        Assert.AreEqual(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(5), grace.GraceUntil - grace.ActivatedAt);
        Assert.IsNull(await service.TransitionAsync(AgentReplayPhase.Grace, grace.Version, Guid.CreateVersion7(), token));
        Assert.IsNull(await service.TransitionAsync(AgentReplayPhase.Enforced, grace.Version, Guid.CreateVersion7(), token));
        Assert.AreEqual(grace, await service.GetAsync(token));
        await using (var db = agents.CreateDbContext())
        {
            Assert.AreEqual(1, await db.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [agent].[ReplayPolicyTestAudit]").SingleAsync(token));
            await db.Database.ExecuteSqlRawAsync("DECLARE @now datetimeoffset = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'); UPDATE [agent].[ReplayPolicies] SET [ActivatedAt] = DATEADD(hour, -25, @now), [GraceUntil] = DATEADD(minute, 5, DATEADD(hour, -1, @now)) WHERE [Scope] = 1", token);
        }
        var due = await service.GetAsync(token);
        var enforced = await service.TransitionAsync(AgentReplayPhase.Enforced, due.Version, Guid.CreateVersion7(), token);
        Assert.AreEqual(AgentReplayPhase.Enforced, enforced!.Phase);
        Assert.IsNull(await service.TransitionAsync(AgentReplayPhase.Grace, enforced.Version, Guid.CreateVersion7(), token));
        await using var verify = agents.CreateDbContext();
        Assert.AreEqual(2, await verify.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM [agent].[ReplayPolicyTestAudit]").SingleAsync(token));
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ReplayPolicy_EnforcementPreservesDuplicatesAndRejectsExpiredBatches(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE [agent].[ReplayPolicyTestAudit] ([Id] int IDENTITY PRIMARY KEY)", token);
        var clock = new TestClock();
        var device = new Device("replay policy", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "test", "X64", ".NET 10");
        var old = new AgentBatch(1, Guid.CreateVersion7(), clock.Now.AddDays(-2), [item]);
        var receiver = new AgentBatchReceiver(agents, identity, clock);
        var service = new AgentReplayPolicyService(agents, new ReplayPolicyTestAuditWriter());
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(old), token));
        var initial = await service.GetAsync(token);
        var grace = await service.TransitionAsync(AgentReplayPhase.Grace, initial.Version, Guid.CreateVersion7(), token);
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(old with { BatchId = Guid.CreateVersion7() }), token));
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("DECLARE @now datetimeoffset = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'); UPDATE [agent].[ReplayPolicies] SET [ActivatedAt] = DATEADD(hour, 1, @now), [GraceUntil] = DATEADD(minute, 5, DATEADD(hour, 25, @now)) WHERE [Scope] = 1", token);
        Assert.AreEqual(BatchReceiptStatus.ClockUncertain, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(old with { BatchId = Guid.CreateVersion7(), EnqueuedAt = clock.Now }), token));
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(old), token));
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("DECLARE @now datetimeoffset = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'); UPDATE [agent].[ReplayPolicies] SET [ActivatedAt] = DATEADD(hour, -25, @now), [GraceUntil] = DATEADD(minute, 5, DATEADD(hour, -1, @now)) WHERE [Scope] = 1", token);
        var due = await service.GetAsync(token);
        Assert.AreEqual(AgentReplayPhase.Grace, grace!.Phase);
        Assert.IsNotNull(await service.TransitionAsync(AgentReplayPhase.Enforced, due.Version, Guid.CreateVersion7(), token));
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(old), token));
        Assert.AreEqual(BatchReceiptStatus.Conflict, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(old with { Items = [item with { RuntimeVersion = "different" }] }), token));
        Assert.AreEqual(BatchReceiptStatus.Expired, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(old with { BatchId = Guid.CreateVersion7() }), token));
        var monitoring = new AgentMonitoring(agents, new DeviceAgentSource(resources), clock);
        Assert.AreEqual(AgentReplayIssue.Expired, (await monitoring.GetAsync(device.Id, token))!.LastReplayIssue!.Reason);
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(old), token));
        Assert.AreEqual(AgentReplayIssue.Expired, (await monitoring.GetAsync(device.Id, token))!.LastReplayIssue!.Reason);
        Assert.AreEqual(BatchReceiptStatus.TimestampAhead, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(old with { BatchId = Guid.CreateVersion7(), EnqueuedAt = clock.Now.AddMinutes(10) }), token));
        Assert.AreEqual(AgentReplayIssue.TimestampAhead, (await monitoring.GetAsync(device.Id, token))!.LastReplayIssue!.Reason);
        var current = AgentBatchCodec.Serialize(old with { BatchId = Guid.CreateVersion7(), EnqueuedAt = clock.Now });
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [agent].[ReceivedBatches] WITH NOCHECK ADD CONSTRAINT [CK_ReplayTestFailure] CHECK ([ByteCount] = -1)", token);
        await Assert.ThrowsAsync<DbUpdateException>(() => receiver.ReceiveAsync(credential, current, token));
        Assert.AreEqual(AgentReplayIssue.TimestampAhead, (await monitoring.GetAsync(device.Id, token))!.LastReplayIssue!.Reason);
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE [agent].[ReceivedBatches] DROP CONSTRAINT [CK_ReplayTestFailure]", token);
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, current, token));
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.LastReplayIssue);
        await using var verify = agents.CreateDbContext();
        Assert.AreEqual(3, await verify.ReceivedBatches.CountAsync(token));
        await verify.Database.ExecuteSqlRawAsync("UPDATE [agent].[ReplayPolicies] SET [Phase] = 1, [EnforcedAt] = NULL WHERE [Scope] = 1", token);
        await verify.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[Agents] SET [ReplayCleanedBefore] = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') WHERE [Id] = {registration.AgentId!.Value}", token);
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(old), token));
        Assert.AreEqual(BatchReceiptStatus.Conflict, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(old with { Items = [item with { RuntimeVersion = "different" }] }), token));
        Assert.AreEqual(BatchReceiptStatus.ClockUncertain, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(old with { BatchId = Guid.CreateVersion7(), EnqueuedAt = clock.Now }), token));
        Assert.AreEqual(3, await verify.ReceivedBatches.CountAsync(token));
    }, snapshotReads);

    [TestMethod]
    public Task ReplayGenerations_PreserveLiveAllowanceWithoutReopeningCleanedRange() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("generation receiver", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("DECLARE @now datetimeoffset = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'); UPDATE [agent].[ReplayPolicies] SET [Phase] = 2, [FreshInstallation] = 1, [ActivatedAt] = DATEADD(hour, -1, @now), [EnforcedAt] = DATEADD(hour, -1, @now) WHERE [Scope] = 1", token);
        var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "test", "X64", ".NET 10");
        var batch = new AgentBatch(1, Guid.CreateVersion7(), clock.Now.AddDays(-2), [item]);
        var bytes = AgentBatchCodec.Serialize(batch);
        var receiver = new AgentBatchReceiver(agents, identity, clock);
        Assert.AreEqual(BatchReceiptStatus.Expired, await receiver.ReceiveAsync(credential, bytes, token));
        await using (var db = agents.CreateDbContext())
        {
            var policy = await db.ReplayPolicies.SingleAsync(token);
            db.ReplayGenerations.Add(new AgentReplayGeneration(policy.Id, 7 * 24 * 60 * 60, DateTimeOffset.UtcNow.AddHours(1)));
            db.ReplayGenerations.Add(new AgentReplayGeneration(policy.Id, 3 * 24 * 60 * 60, DateTimeOffset.UtcNow.AddHours(1)));
            db.ReplayGenerations.Add(new AgentReplayGeneration(policy.Id, 30 * 24 * 60 * 60, DateTimeOffset.UtcNow.AddHours(-1)));
            await db.SaveChangesAsync(token);
        }
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, bytes, token));
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7(), EnqueuedAt = clock.Now.AddDays(-6) }), token));
        Assert.AreEqual(BatchReceiptStatus.Expired, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7(), EnqueuedAt = clock.Now.AddDays(-10) }), token));
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[Agents] SET [ReplayCleanedBefore] = DATEADD(day, -1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [Id] = {registration.AgentId.Value}", token);
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, bytes, token));
        Assert.AreEqual(BatchReceiptStatus.Expired, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7() }), token));
        await using (var db = agents.CreateDbContext())
        {
            Assert.AreEqual(2, await db.ReceivedBatches.CountAsync(token));
            Assert.AreEqual(1, await db.RuntimeObservations.CountAsync(token));
        }
    });

    [TestMethod]
    public Task ReplayGenerationsMigration_RejectsDowngradeWhenPromisesExist() => WithDatabase(async (_, agents, token) =>
    {
        await using (var db = agents.CreateDbContext())
        {
            var policy = await db.ReplayPolicies.SingleAsync(token);
            db.ReplayGenerations.Add(new AgentReplayGeneration(policy.Id, 172800, DateTimeOffset.UtcNow.AddDays(2)));
            await db.SaveChangesAsync(token);
        }
        async Task AssertDowngradeRejectedAsync()
        {
            await using var db = agents.CreateDbContext();
            var error = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync("20261109165532_AgentInboxSafety", token));
            Assert.AreEqual(51000, error.Number);
        }
        await AssertDowngradeRejectedAsync();
        await using (var db = agents.CreateDbContext())
        {
            await db.ReplayGenerations.ExecuteDeleteAsync(token);
            await db.Database.ExecuteSqlRawAsync("UPDATE [agent].[ReplayPolicies] SET [ReplayWindowSeconds] = 172800 WHERE [Scope] = 1", token);
        }
        await AssertDowngradeRejectedAsync();
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE [agent].[ReplayPolicies] SET [ReplayWindowSeconds] = 86400, [ReceiptRetentionDays] = 8 WHERE [Scope] = 1", token);
        await AssertDowngradeRejectedAsync();
    });

    [TestMethod]
    public Task ReplayPolicy_DowngradeCannotRaceAnActivation() => WithDatabase(async (resources, agents, token) =>
    {
        var writer = new BlockingReplayAuditWriter();
        var service = new AgentReplayPolicyService(agents, writer);
        var initial = await service.GetAsync(token);
        var activation = service.TransitionAsync(AgentReplayPhase.Grace, initial.Version, Guid.CreateVersion7(), token);
        try
        {
            await writer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            await using var migrationDb = agents.CreateDbContext();
            var downgrade = migrationDb.GetService<IMigrator>().MigrateAsync("20261103193528_AgentReplayClock", token);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), token);
                Assert.IsFalse(downgrade.IsCompleted, "Downgrade must wait for the active policy transaction.");
            }
            finally { writer.Release.TrySetResult(); }
            Assert.IsNotNull(await activation);
            var failure = await Assert.ThrowsAsync<SqlException>(() => downgrade);
            Assert.AreEqual(51000, failure.Number);
        }
        finally { writer.Release.TrySetResult(); }
    });

    private sealed class BlockingReplayAuditWriter : IOperationAuditWriter<OperationAudit>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ReplayPolicyTestAuditWriter : IOperationAuditWriter<OperationAudit>
    {
        public bool Fail { get; set; }
        public async Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO [agent].[ReplayPolicyTestAudit] DEFAULT VALUES";
            await command.ExecuteNonQueryAsync(cancellationToken);
            if (Fail) throw new InvalidOperationException("Injected audit failure.");
        }
    }

    [TestMethod]
    public Task ReplayClockMigration_InitializesExistingReceiptHistory() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var firstDevice = new Device("receipt owner", "127.0.0.1", true, clock.Now);
        var emptyDevice = new Device("no receipt", "127.0.0.2", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.AddRange(firstDevice, emptyDevice); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        async Task<(Guid Id, string Credential)> RegisterAsync(Guid deviceId)
        {
            var issued = await identity.IssueAsync(deviceId, Guid.CreateVersion7(), token);
            var secret = RandomNumberGenerator.GetBytes(32);
            var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
            return (registered.AgentId!.Value, Credential(registered.AgentId.Value, secret));
        }
        var first = await RegisterAsync(firstDevice.Id);
        var empty = await RegisterAsync(emptyDevice.Id);
        var message = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "test", "X64", ".NET 10");
        var batch = new AgentBatch(1, Guid.CreateVersion7(), clock.Now, [message]);
        Assert.AreEqual(BatchReceiptStatus.Accepted, await new AgentBatchReceiver(agents, identity, clock).ReceiveAsync(first.Credential, AgentBatchCodec.Serialize(batch), token));
        await using (var db = agents.CreateDbContext())
            await db.GetService<IMigrator>().MigrateAsync("20261103181531_AgentQueueCapacity", token);
        await using (var db = agents.CreateDbContext())
            await db.Database.MigrateAsync(token);
        await using var verify = agents.CreateDbContext();
        Assert.AreEqual(clock.Now, (await verify.Agents.SingleAsync(x => x.Id == first.Id, token)).ReplayClockFloor);
        Assert.IsNull((await verify.Agents.SingleAsync(x => x.Id == empty.Id, token)).ReplayClockFloor);
        Assert.AreEqual(1, await verify.ReceivedBatches.CountAsync(token));
    });

    [TestMethod]
    public Task InboxDiagnostics_SeesLegacyReceiverAfterProjectionAtSameTime() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("mixed receiver", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var agentId = registration.AgentId!.Value;
        var message = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "test", "X64", ".NET 10");
        var batch = new AgentBatch(1, Guid.CreateVersion7(), clock.Now, [message]);
        var receiver = new AgentBatchReceiver(agents, identity, clock);
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(Credential(agentId, secret), AgentBatchCodec.Serialize(batch), token));
        var laterEnqueued = clock.Now.AddMinutes(1);
        await using (var db = agents.CreateDbContext())
        {
            var first = await db.ReceivedBatches.SingleAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[Agents] SET [LastBatchEnqueuedAt] = {first.EnqueuedAt}, [LastBatchReceivedAt] = {first.ReceivedAt}, [LastBatchReceiptRowId] = {first.RowId} WHERE [Id] = {agentId}", token);
            db.ReceivedBatches.Add(new ReceivedBatch(agentId, Guid.CreateVersion7(), new byte[32], laterEnqueued, clock.Now, 1, 1));
            await db.SaveChangesAsync(token);
        }
        var monitoring = new AgentMonitoring(agents, new DeviceAgentSource(resources), clock);
        var status = await monitoring.GetAsync(device.Id, token);
        Assert.IsNotNull(status!.LastBatchTime);
        Assert.AreEqual(laterEnqueued, status.LastBatchTime.EnqueuedAt);
        Assert.AreEqual(clock.Now, await new AgentReceiverMonitoring(agents).LastCommittedBatchAsync(token));
        await using (var db = agents.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20261109172036_AgentLastBatchProjection", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[Agents] SET [LastBatchEnqueuedAt] = {clock.Now} WHERE [Id] = {agentId}", token);
            await db.Database.MigrateAsync(token);
        }
        await using var verify = agents.CreateDbContext();
        var projected = await verify.Agents.SingleAsync(x => x.Id == agentId, token);
        var latest = await verify.ReceivedBatches.OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.RowId).FirstAsync(token);
        Assert.AreEqual(latest.RowId, projected.LastBatchReceiptRowId);
        Assert.AreEqual(laterEnqueued, projected.LastBatchEnqueuedAt);
    });

    [TestMethod]
    public Task InboxCleanup_DeletesBoundedReceiptsAndPreservesReplayFence() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock { Now = DateTimeOffset.UtcNow.AddDays(-10) };
        var device = new Device("cleanup server", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var agentId = registered.AgentId!.Value;
        var credential = Credential(agentId, secret);
        var receiver = new AgentBatchReceiver(agents, identity, clock);
        AgentBatch CreateBatch() => new(1, Guid.CreateVersion7(), clock.Now,
            [new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "test", "X64", ".NET 10")]);
        var first = CreateBatch();
        var second = CreateBatch();
        var retained = CreateBatch();
        foreach (var batch in new[] { first, second, retained })
            Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(batch), token));
        var cleanup = new AgentInboxCleanup(agents);
        Assert.AreEqual(0, await cleanup.CleanAgentAsync(agentId, 1, token));
        await using (var db = agents.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("DECLARE @now datetimeoffset = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'); UPDATE [agent].[ReplayPolicies] SET [Phase] = 2, [FreshInstallation] = 1, [ActivatedAt] = DATEADD(day, -10, @now), [EnforcedAt] = DATEADD(day, -10, @now), [CleanupCutoverAt] = DATEADD(day, -9, @now), [InboxSettingsChangedAt] = DATEADD(day, -9, @now), [LegacyReceiptsRetainUntil] = DATEADD(day, -2, @now) WHERE [Scope] = 1", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[ReceivedBatches] SET [RetainUntil] = DATEADD(day, -1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [AgentId] = {agentId} AND [BatchId] <> {retained.BatchId}", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[Agents] SET [ReplayClockFloor] = DATEADD(day, 1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [Id] = {agentId}", token);
        }
        Assert.AreEqual(0, await cleanup.CleanAgentAsync(agentId, 1, token));
        await using (var db = agents.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[Agents] SET [ReplayClockFloor] = DATEADD(day, -1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [Id] = {agentId}", token);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER [agent].[FailCleanupFence] ON [agent].[Agents] AFTER UPDATE AS THROW 51001, 'Injected failure', 1", token);
        }
        await Assert.ThrowsAsync<DbUpdateException>(() => cleanup.CleanAgentAsync(agentId, 1, token));
        await using (var db = agents.CreateDbContext())
        {
            Assert.AreEqual(3, await db.ReceivedBatches.CountAsync(token));
            Assert.IsNull((await db.Agents.SingleAsync(x => x.Id == agentId, token)).ReplayCleanedBefore);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER [agent].[FailCleanupFence]", token);
        }
        Assert.AreEqual(1, await cleanup.CleanAgentAsync(agentId, 1, token));
        Assert.AreEqual(BatchReceiptStatus.Expired, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(first), token));
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(second), token));
        Assert.AreEqual(1, await cleanup.CleanAgentAsync(agentId, 1, token));
        Assert.AreEqual(0, await cleanup.CleanAgentAsync(agentId, 1, token));
        await using var verify = agents.CreateDbContext();
        Assert.AreEqual(retained.BatchId, (await verify.ReceivedBatches.SingleAsync(token)).BatchId);
        Assert.AreEqual(3, await verify.RuntimeObservations.CountAsync(token));
        Assert.IsNotNull((await verify.Agents.SingleAsync(x => x.Id == agentId, token)).ReplayCleanedBefore);
        await verify.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[ReceivedBatches] SET [RetainUntil] = DATEADD(day, -1, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE [AgentId] = {agentId}", token);
        Assert.AreEqual(1, await cleanup.CleanAgentAsync(agentId, 1, token));
        var monitoring = new AgentMonitoring(agents, new DeviceAgentSource(resources), clock);
        Assert.AreEqual(clock.Now, (await monitoring.GetAsync(device.Id, token))!.LastCommittedBatchAt);
        Assert.AreEqual(clock.Now, await new AgentReceiverMonitoring(agents).LastCommittedBatchAsync(token));
    });

    [TestMethod]
    public Task InboxReceipt_RetainsOriginalDeadlineWhenPolicyShortens() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock { Now = DateTimeOffset.UtcNow.AddYears(-1) };
        var device = new Device("retention server", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registered.AgentId!.Value, secret);
        var receiver = new AgentBatchReceiver(agents, identity, clock);
        AgentBatch CreateBatch() => new(1, Guid.CreateVersion7(), clock.Now,
            [new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "test", "X64", ".NET 10")]);
        var first = CreateBatch();
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(first), token));
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("UPDATE [agent].[ReplayPolicies] SET [ReceiptRetentionDays] = 2 WHERE [Scope] = 1", token);
        var second = CreateBatch();
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(second), token));
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(first), token));
        await using var verify = agents.CreateDbContext();
        var receipts = await verify.ReceivedBatches.AsNoTracking().ToDictionaryAsync(x => x.BatchId, token);
        var oldReceipt = receipts[first.BatchId];
        var newReceipt = receipts[second.BatchId];
        Assert.IsNotNull(oldReceipt.TrustedReceivedAt);
        Assert.IsNotNull(newReceipt.TrustedReceivedAt);
        Assert.AreEqual(oldReceipt.TrustedReceivedAt.Value.AddDays(7), oldReceipt.RetainUntil);
        Assert.AreEqual(newReceipt.TrustedReceivedAt.Value.AddDays(2), newReceipt.RetainUntil);
        Assert.AreEqual(clock.Now, oldReceipt.ReceivedAt);
        Assert.IsGreaterThan(clock.Now.AddMonths(11), oldReceipt.TrustedReceivedAt.Value);
    });

    [TestMethod]
    public Task InboxSafetyMigration_PreservesLegacyReceiptsWithoutInventingTrustedTimes() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock { Now = DateTimeOffset.UtcNow.AddYears(-1) };
        var device = new Device("legacy receipt", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        var message = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "test", "X64", ".NET 10");
        var batch = new AgentBatch(1, Guid.CreateVersion7(), clock.Now, [message]);
        var bytes = AgentBatchCodec.Serialize(batch);
        Assert.AreEqual(BatchReceiptStatus.Accepted, await new AgentBatchReceiver(agents, identity, clock).ReceiveAsync(credential, bytes, token));
        await using (var db = agents.CreateDbContext())
        {
            var receipt = await db.ReceivedBatches.SingleAsync(token);
            var databaseNow = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
            Assert.AreEqual(clock.Now, receipt.ReceivedAt);
            Assert.IsNotNull(receipt.TrustedReceivedAt);
            Assert.IsLessThan(TimeSpan.FromMinutes(1), (databaseNow - receipt.TrustedReceivedAt.Value).Duration());
        }
        await using (var db = agents.CreateDbContext())
            await db.GetService<IMigrator>().MigrateAsync("20261103195754_AgentReplayFreshInstallation", token);
        await using (var db = agents.CreateDbContext()) await db.Database.MigrateAsync(token);
        await using (var db = agents.CreateDbContext())
        {
            var receipt = await db.ReceivedBatches.SingleAsync(token);
            Assert.AreEqual(batch.BatchId, receipt.BatchId);
            Assert.IsNull(receipt.TrustedReceivedAt);
            Assert.IsNull(receipt.RetainUntil);
            var historicalAgent = await db.Agents.SingleAsync(token);
            Assert.AreEqual(receipt.ReceivedAt, historicalAgent.LastBatchReceivedAt);
            Assert.AreEqual(receipt.EnqueuedAt, historicalAgent.LastBatchEnqueuedAt);
            Assert.IsNull((await db.Agents.SingleAsync(token)).ReplayCleanedBefore);
            Assert.IsNull((await db.ReplayPolicies.SingleAsync(token)).CleanupCutoverAt);
            Assert.AreEqual(1, await db.RuntimeObservations.CountAsync(token));
        }
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted,
            await new AgentBatchReceiver(agents, identity, clock).ReceiveAsync(credential, bytes, token));
        await using (var db = agents.CreateDbContext())
            await db.Database.ExecuteSqlRawAsync("DECLARE @now datetimeoffset = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'); UPDATE [agent].[ReplayPolicies] SET [Phase] = 2, [FreshInstallation] = 1, [ActivatedAt] = @now, [EnforcedAt] = @now, [CleanupCutoverAt] = @now, [InboxSettingsChangedAt] = @now, [LegacyReceiptsRetainUntil] = DATEADD(day, 7, @now) WHERE [Scope] = 1", token);
        await using (var db = agents.CreateDbContext())
        {
            var rejected = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync("20261109170951_AgentInboxLegacyConstraint", token));
            Assert.AreEqual(51000, rejected.Number);
        }
        await using (var db = agents.CreateDbContext())
        {
            Assert.IsNotNull((await db.ReplayPolicies.SingleAsync(token)).CleanupCutoverAt);
            Assert.AreEqual(1, await db.ReceivedBatches.CountAsync(token));
        }
        await using (var db = agents.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE [agent].[ReplayPolicies] SET [CleanupCutoverAt] = NULL, [InboxSettingsChangedAt] = NULL, [LegacyReceiptsRetainUntil] = NULL WHERE [Scope] = 1", token);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [agent].[Agents] SET [ReplayCleanedBefore] = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') WHERE [Id] = {registration.AgentId.Value}", token);
        }
        await using (var db = agents.CreateDbContext())
        {
            var rejected = await Assert.ThrowsAsync<SqlException>(() => db.GetService<IMigrator>().MigrateAsync("20261103195754_AgentReplayFreshInstallation", token));
            Assert.AreEqual(51000, rejected.Number);
        }
    });

    [TestMethod]
    public Task Batches_AcknowledgmentRetrySucceedsEvenWhenCapacityIsFull() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("batch server", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registered.AgentId!.Value, secret);
        var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "Example OS", "X64", ".NET 10");
        var batch = new AgentBatch(1, Guid.CreateVersion7(), clock.Now, [item]);
        var payload = AgentBatchCodec.Serialize(batch);
        var receiver = new AgentBatchReceiver(agents, identity, clock, capacityBytes: payload.Length, maximumBatches: 1);
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, payload, token));
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, payload, token));
        Assert.AreEqual(BatchReceiptStatus.CapacityExceeded, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7() }), token));
        await using var verify = agents.CreateDbContext();
        Assert.AreEqual(1, await verify.ReceivedBatches.CountAsync(token));
        Assert.AreEqual(1, await verify.RuntimeObservations.CountAsync(token));
        var next = AgentBatchCodec.Serialize(batch with { BatchId = Guid.CreateVersion7(), Items = [item with { MessageId = Guid.CreateVersion7() }] });
        Assert.HasCount(payload.Length, next);
        var byteLimited = new AgentBatchReceiver(agents, identity, clock, capacityBytes: payload.Length + next.Length - 1L, maximumBatches: 10);
        Assert.AreEqual(BatchReceiptStatus.CapacityExceeded, await byteLimited.ReceiveAsync(credential, next, token));
        var exactCapacity = new AgentBatchReceiver(agents, identity, clock, capacityBytes: payload.Length + next.Length, maximumBatches: 10);
        Assert.AreEqual(BatchReceiptStatus.Accepted, await exactCapacity.ReceiveAsync(credential, next, token));
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Batches_TimeObservationUsesOneLatestReceiptWithoutRefreshingOnRetry(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("clock observation", "127.0.0.8", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var source = new DeviceAgentSource(resources);
        var identity = new AgentIdentity(agents, source, clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        var receiver = new AgentBatchReceiver(agents, identity, clock);
        var monitoring = new AgentMonitoring(agents, source, clock);
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.LastBatchTime);
        var received = clock.Now;
        var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, received, "test", "test", "test");
        var boundary = new AgentBatch(1, Guid.CreateVersion7(), received.AddMinutes(5), [item]);
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(boundary), token));
        var status = (await monitoring.GetAsync(device.Id, token))!.LastBatchTime!;
        Assert.IsFalse(status.EnqueuedAheadOfReceipt);
        Assert.AreEqual(boundary.EnqueuedAt, status.EnqueuedAt);
        Assert.AreEqual(received, status.ReceivedAt);
        var future = boundary with { BatchId = Guid.CreateVersion7(), EnqueuedAt = received.AddMinutes(5).AddTicks(1), Items = [item with { MessageId = Guid.CreateVersion7() }] };
        var payload = AgentBatchCodec.Serialize(future);
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, payload, token));
        status = (await monitoring.GetAsync(device.Id, token))!.LastBatchTime!;
        Assert.IsTrue(status.EnqueuedAheadOfReceipt, "The later RowId breaks identical receipt-time ties.");
        Assert.AreEqual(future.EnqueuedAt, status.EnqueuedAt);
        clock.Now = received.AddMinutes(1);
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, payload, token));
        Assert.AreEqual(status, (await monitoring.GetAsync(device.Id, token))!.LastBatchTime, "A duplicate does not change historical observations.");
        var older = boundary with { BatchId = Guid.CreateVersion7(), EnqueuedAt = received.AddDays(-1), Items = [item with { MessageId = Guid.CreateVersion7() }] };
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(older), token));
        var latest = (await monitoring.GetAsync(device.Id, token))!.LastBatchTime!;
        Assert.IsFalse(latest.EnqueuedAheadOfReceipt, "Later received historical batches are not evidence of the current clock.");
        Assert.AreEqual(older.EnqueuedAt, latest.EnqueuedAt);
        Assert.AreEqual(clock.Now, latest.ReceivedAt);
        clock.Now = received.AddMinutes(-1);
        Assert.IsTrue((await monitoring.GetAsync(device.Id, token))!.LastBatchTime!.ReceivedAt > clock.Now, "A platform clock rollback leaves the observation visible.");
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RevokeAsync(registration.AgentId.Value, Guid.CreateVersion7(), token));
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.LastBatchTime);
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Batches_ConcurrentNewBatchesCannotExceedCapacity(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("capacity server", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "Example OS", "X64", ".NET 10");
        var first = AgentBatchCodec.Serialize(new AgentBatch(1, Guid.CreateVersion7(), clock.Now, [item]));
        var second = AgentBatchCodec.Serialize(new AgentBatch(1, Guid.CreateVersion7(), clock.Now, [item with { MessageId = Guid.CreateVersion7() }]));
        var receiver = new AgentBatchReceiver(agents, identity, clock, maximumBatches: 1);
        var results = await Task.WhenAll(receiver.ReceiveAsync(credential, first, token), receiver.ReceiveAsync(credential, second, token));
        Assert.AreEqual(1, results.Count(x => x == BatchReceiptStatus.Accepted));
        Assert.AreEqual(1, results.Count(x => x == BatchReceiptStatus.CapacityExceeded));
        Assert.AreEqual(BatchReceiptStatus.AlreadyAccepted, await receiver.ReceiveAsync(credential, results[0] == BatchReceiptStatus.Accepted ? first : second, token));
        await using var verify = agents.CreateDbContext();
        Assert.AreEqual(1, await verify.ReceivedBatches.CountAsync(token));
        Assert.AreEqual(1, await verify.RuntimeObservations.CountAsync(token));
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Batches_RejectCredentialRevokedDuringTransaction(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("revoked batch server", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "Example OS", "X64", ".NET 10");
        var payload = AgentBatchCodec.Serialize(new AgentBatch(1, Guid.CreateVersion7(), clock.Now, [item]));
        await using var revoke = agents.CreateDbContext();
        await using var transaction = await revoke.Database.BeginTransactionAsync(token);
        await using (var command = revoke.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = $"vigiloom.agent.device.{device.Id:D}"; command.Parameters.Add(parameter);
            Assert.IsGreaterThanOrEqualTo(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
        }
        (await revoke.Agents.SingleAsync(x => x.Id == registration.AgentId, token)).Revoke(clock.Now);
        await revoke.SaveChangesAsync(token);
        var waiting = new AgentBatchReceiver(agents, identity, clock).ReceiveAsync(credential, payload, token);
        await Task.Delay(250, token);
        Assert.IsFalse(waiting.IsCompleted, "The batch must wait for the revocation transaction.");
        await transaction.CommitAsync(token);
        Assert.AreEqual(BatchReceiptStatus.Unauthorized, await waiting);
        await using var verify = agents.CreateDbContext();
        Assert.AreEqual(0, await verify.ReceivedBatches.CountAsync(token));
        Assert.AreEqual(0, await verify.RuntimeObservations.CountAsync(token));
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task QueueSession_ConcurrentReplacementAllowsOnlyOneWinner(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("queue session server", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var source = new DeviceAgentSource(resources);
        var identity = new AgentIdentity(agents, source, clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        var original = Guid.CreateVersion7();
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.BeginQueueSessionAsync(credential, new AgentQueueSessionRequest(null, original), token));
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var results = await Task.WhenAll(
            identity.BeginQueueSessionAsync(credential, new AgentQueueSessionRequest(original, first), token),
            identity.BeginQueueSessionAsync(credential, new AgentQueueSessionRequest(original, second), token));
        Assert.AreEqual(1, results.Count(x => x == AgentIdentityStatus.Success));
        Assert.AreEqual(1, results.Count(x => x == AgentIdentityStatus.Conflict));
        var winner = results[0] == AgentIdentityStatus.Success ? first : second;
        var queue = new AgentQueueSnapshot(10, 1, 0, 1024, 10);
        Assert.AreEqual(AgentIdentityStatus.Conflict, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, original, 1, queue), token));
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, winner, 1, queue), token));
        Assert.AreEqual(queue, (await new AgentMonitoring(agents, source, clock).GetAsync(device.Id, token))!.Queue!.Snapshot);
    }, snapshotReads);

    [TestMethod]
    public Task AgentMonitoring_DistinguishesRegistrationHeartbeatAndOfflineFromPing() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("monitored server", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var source = new DeviceAgentSource(resources);
        var identity = new AgentIdentity(agents, source, clock);
        var monitoring = new AgentMonitoring(agents, source, clock);
        Assert.IsNull(await monitoring.GetAsync(Guid.CreateVersion7(), token));
        Assert.AreEqual(AgentConnectionState.NotRegistered, (await monitoring.GetAsync(device.Id, token))!.State);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        Assert.AreEqual(AgentConnectionState.AwaitingFirstHeartbeat, (await monitoring.GetAsync(device.Id, token))!.State);
        clock.Now = clock.Now.AddSeconds(90);
        Assert.AreEqual(AgentConnectionState.Offline, (await monitoring.GetAsync(device.Id, token))!.State);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, token));
        var receivedAt = clock.Now;
        Assert.AreEqual(AgentConnectionState.Online, (await monitoring.GetAsync(device.Id, token))!.State);
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.Queue);
        var epoch = Guid.CreateVersion7();
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.BeginQueueSessionAsync(credential, new AgentQueueSessionRequest(null, epoch), token));
        var queue = new AgentQueueSnapshot(1024, 3, 1, 4096, 10);
        var reasons = new AgentQuarantineBreakdown(0, 0, 1, 0, 0, 0, 0);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 2, queue, Quarantine: reasons), token));
        var queueStatus = (await monitoring.GetAsync(device.Id, token))!.Queue;
        Assert.AreEqual(receivedAt, queueStatus!.ReportedAt);
        Assert.AreEqual(queue, queueStatus.Snapshot);
        Assert.AreEqual(reasons, queueStatus.Quarantine);
        await using (var legacy = agents.CreateDbContext())
            await legacy.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent.Agents SET QueueReportSequence = {3L} WHERE Id = {registration.AgentId!.Value}", token);
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.Queue!.Quarantine, "A legacy server can advance the queue without clearing new columns.");
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 4, queue, Quarantine: reasons), token));
        var legacyEpoch = Guid.CreateVersion7();
        await using (var legacy = agents.CreateDbContext())
            await legacy.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent.Agents SET QueueReportEpoch = {legacyEpoch} WHERE Id = {registration.AgentId!.Value}", token);
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.Queue!.Quarantine, "A legacy session replacement must not expose the previous session's details.");
        await using (var legacy = agents.CreateDbContext())
            await legacy.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent.Agents SET QueueReportEpoch = {epoch} WHERE Id = {registration.AgentId!.Value}", token);
        clock.Now = receivedAt.AddSeconds(1);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 1, null), token));
        Assert.AreEqual(queue, (await monitoring.GetAsync(device.Id, token))!.Queue!.Snapshot, "A late older sequence must not replace newer queue status.");
        Assert.AreEqual(reasons, (await monitoring.GetAsync(device.Id, token))!.Queue!.Quarantine);
        clock.Now = receivedAt.AddSeconds(-1);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 5, queue), token));
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.Queue!.Quarantine, "An old Agent's newer report must not reuse previous details.");
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 6, null), token));
        queueStatus = (await monitoring.GetAsync(device.Id, token))!.Queue;
        Assert.AreEqual(clock.Now, queueStatus!.ReportedAt);
        Assert.IsNull(queueStatus.Snapshot, "Unavailable must differ from an empty queue.");
        await Assert.ThrowsAsync<ArgumentException>(() => identity.RecordHeartbeatAsync(credential,
            new AgentHeartbeatReport(1, epoch, 7, queue with { QuarantinedBatches = 4 }), token));
        await Assert.ThrowsAsync<ArgumentException>(() => identity.RecordHeartbeatAsync(credential,
            new AgentHeartbeatReport(1, epoch, 7, queue, Quarantine: reasons with { Conflict = 2 }), token));
        await Assert.ThrowsAsync<ArgumentException>(() => identity.RecordHeartbeatAsync(credential,
            new AgentHeartbeatReport(1, epoch, 7, null, Quarantine: reasons), token));
        var replacementEpoch = Guid.CreateVersion7();
        var replacement = new AgentQueueSessionRequest(epoch, replacementEpoch);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.BeginQueueSessionAsync(credential, replacement, token));
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.BeginQueueSessionAsync(credential, replacement, token), "A lost session response can be retried.");
        Assert.AreEqual(AgentIdentityStatus.Conflict, await identity.BeginQueueSessionAsync(credential, new AgentQueueSessionRequest(epoch, Guid.CreateVersion7()), token));
        Assert.AreEqual(AgentIdentityStatus.Conflict, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 4, queue), token));
        Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.Queue, "An old session cannot restore stale queue status.");
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, replacementEpoch, 1, queue), token));
        Assert.AreEqual(queue, (await monitoring.GetAsync(device.Id, token))!.Queue!.Snapshot);
        var latestHeartbeat = receivedAt.AddSeconds(1);
        clock.Now = latestHeartbeat.AddSeconds(89);
        Assert.AreEqual(AgentConnectionState.Online, (await monitoring.GetAsync(device.Id, token))!.State);
        clock.Now = clock.Now.AddSeconds(1);
        var offline = await monitoring.GetAsync(device.Id, token);
        Assert.AreEqual(AgentConnectionState.Offline, offline!.State);
        Assert.AreEqual(latestHeartbeat, offline.LastHeartbeatAt);
        clock.Now = latestHeartbeat.AddSeconds(-1);
        Assert.AreEqual(AgentConnectionState.ClockUncertain, (await monitoring.GetAsync(device.Id, token))!.State);
        clock.Now = latestHeartbeat.AddMinutes(-6);
        Assert.AreEqual(AgentConnectionState.ClockUncertain, (await monitoring.GetAsync(device.Id, token))!.State);
        await using (var db = resources.CreateDbContext())
        {
            var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            current.Update(current.Name, current.Address, true, false, clock.Now); await db.SaveChangesAsync(token);
        }
        Assert.AreEqual(AgentConnectionState.Disabled, (await monitoring.GetAsync(device.Id, token))!.State);
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Heartbeat_RevalidatesCredentialWhenConcurrentRevocationCommits(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("heartbeat server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        Assert.AreEqual(AgentIdentityStatus.Success, registration.Status);
        var credential = Credential(registration.AgentId!.Value, secret);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, token));
        var first = clock.Now;
        clock.Now = clock.Now.AddMinutes(1);
        await using var revoke = agents.CreateDbContext();
        await using var transaction = await revoke.Database.BeginTransactionAsync(token);
        await using (var command = revoke.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = $"vigiloom.agent.device.{device.Id:D}"; command.Parameters.Add(parameter);
            Assert.IsGreaterThanOrEqualTo(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
        }
        var entity = await revoke.Agents.SingleAsync(x => x.Id == registration.AgentId, token);
        entity.Revoke(clock.GetUtcNow());
        await revoke.SaveChangesAsync(token);
        var waiting = identity.RecordHeartbeatAsync(credential, token);
        await Task.Delay(250, token);
        Assert.IsFalse(waiting.IsCompleted, "The heartbeat must wait for the revocation transaction.");
        await transaction.CommitAsync(token);
        Assert.AreEqual(AgentIdentityStatus.Invalid, await waiting);
        await using var check = agents.CreateDbContext();
        Assert.AreEqual(first, (await check.Agents.SingleAsync(x => x.Id == registration.AgentId, token)).LastHeartbeatAt);
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Heartbeat_RejectsConcurrentDeviceArchive(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("heartbeat server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registration.AgentId!.Value, secret);
        await using var archive = resources.CreateDbContext();
        await using var transaction = await archive.Database.BeginTransactionAsync(token);
        var current = await archive.Devices.SingleAsync(x => x.Id == device.Id, token);
        current.Archive(clock.GetUtcNow()); await archive.SaveChangesAsync(token);
        var waiting = identity.RecordHeartbeatAsync(credential, token);
        await Task.Delay(250, token);
        Assert.IsFalse(waiting.IsCompleted, "The heartbeat must wait for the archive transaction.");
        await transaction.CommitAsync(token);
        Assert.AreEqual(AgentIdentityStatus.Invalid, await waiting);
        await using var check = agents.CreateDbContext();
        Assert.IsNull((await check.Agents.SingleAsync(x => x.Id == registration.AgentId, token)).LastHeartbeatAt);
    }, snapshotReads);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Heartbeat_RejectsOldCredentialAfterConcurrentRotation(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("heartbeat server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var oldSecret = RandomNumberGenerator.GetBytes(32);
        var nextSecret = RandomNumberGenerator.GetBytes(32);
        var registration = await identity.RedeemAsync(issued.Secret, SHA256.HashData(oldSecret), token);
        var oldCredential = Credential(registration.AgentId!.Value, oldSecret);
        var nextCredential = Credential(registration.AgentId.Value, nextSecret);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.BeginRotationAsync(oldCredential, SHA256.HashData(nextSecret), token));
        await using var rotation = agents.CreateDbContext();
        await using var transaction = await rotation.Database.BeginTransactionAsync(token);
        await using (var command = rotation.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @name, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r";
            var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = $"vigiloom.agent.device.{device.Id:D}"; command.Parameters.Add(parameter);
            Assert.IsGreaterThanOrEqualTo(0, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
        }
        var current = await rotation.Agents.SingleAsync(x => x.Id == registration.AgentId, token);
        current.FinishRotation(); await rotation.SaveChangesAsync(token);
        var waiting = identity.RecordHeartbeatAsync(oldCredential, token);
        await Task.Delay(250, token);
        Assert.IsFalse(waiting.IsCompleted, "The heartbeat must wait for the rotation transaction.");
        await transaction.CommitAsync(token);
        Assert.AreEqual(AgentIdentityStatus.Invalid, await waiting);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(nextCredential, token));
    }, snapshotReads);

    [TestMethod]
    public Task SchemaUsesResourceKeysClusteredRowsAndDeviceForeignKeys() => WithDatabase(async (_, agents, token) =>
    {
        await using var db = agents.CreateDbContext();
        await db.Database.OpenConnectionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM sys.indexes i
            JOIN sys.tables t ON i.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'agent' AND t.name IN ('Agents', 'Enrollments')
              AND ((i.is_primary_key = 1 AND i.type_desc = 'NONCLUSTERED') OR (i.is_unique = 1 AND i.type_desc = 'CLUSTERED'))
            """;
        Assert.AreEqual(4, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
        command.CommandText = """
            SELECT COUNT(*) FROM sys.foreign_keys f
            JOIN sys.tables t ON f.parent_object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'agent' AND f.delete_referential_action_desc = 'NO_ACTION'
              AND f.name IN ('FK_Agents_Devices_DeviceId', 'FK_Enrollments_Devices_DeviceId', 'FK_Enrollments_Agents_AgentId',
                             'FK_AgentAudits_Devices_DeviceId', 'FK_AgentAudits_Agents_AgentId')
            """;
        Assert.AreEqual(5, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
        command.CommandText = """
            SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables t ON i.object_id = t.object_id JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'agent' AND t.name = 'Agents' AND i.name = 'IX_Agents_DeviceId' AND i.is_unique = 1 AND i.has_filter = 1
            """;
        Assert.AreEqual(1, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
        command.CommandText = """
            SELECT COUNT(*) FROM sys.columns c JOIN sys.tables t ON c.object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE s.name = 'agent' AND t.name = 'Agents' AND c.name = 'LastHeartbeatAt'
              AND c.system_type_id = TYPE_ID('datetimeoffset') AND c.scale = 7 AND c.is_nullable = 1
            """;
        Assert.AreEqual(1, Convert.ToInt32(await command.ExecuteScalarAsync(token)));
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task EnrollmentRetriesAndArchiveNeverRevivesCredential(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var now = new TestClock();
        var device = new Device("server", "127.0.0.1", true, now.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), now);
        var issued = await service.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        Assert.AreEqual(AgentIdentityStatus.Success, issued.Status);
        var secret = RandomNumberGenerator.GetBytes(32);
        var verifier = SHA256.HashData(secret);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => service.RedeemAsync(issued.Secret, verifier, token)));
        Assert.IsTrue(attempts.All(x => x.Status == AgentIdentityStatus.Success));
        Assert.AreEqual(attempts[0].AgentId, attempts[1].AgentId);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RedeemAsync(issued.Secret, RandomNumberGenerator.GetBytes(32), token)).Status);
        var credential = $"vla_{attempts[0].AgentId:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        Assert.IsNotNull(await service.AuthenticateAsync(credential, token));
        Assert.IsNull(await service.AuthenticateAsync(issued.Secret, token));
        await using (var db = resources.CreateDbContext())
        {
            var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            current.Archive(now.GetUtcNow()); await db.SaveChangesAsync(token);
            current.Restore(now.GetUtcNow()); current.Update(current.Name, current.Address, true, true, now.GetUtcNow()); await db.SaveChangesAsync(token);
        }
        Assert.IsNull(await service.AuthenticateAsync(credential, token));
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RedeemAsync(issued.Secret, verifier, token)).Status);
        var replacement = await service.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        Assert.AreEqual(AgentIdentityStatus.Success, replacement.Status);
        Assert.AreEqual(AgentIdentityStatus.Success, (await service.RedeemAsync(replacement.Secret, RandomNumberGenerator.GetBytes(32), token)).Status);
        await using var check = agents.CreateDbContext();
        Assert.AreEqual(2, await check.Agents.CountAsync(token));
        Assert.AreEqual(1, await check.Agents.CountAsync(x => x.RevokedAt == null, token));
    }, snapshotReads);

    [TestMethod]
    public Task CompetingMachinesCannotRedeemSameEnrollment() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await service.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var results = await Task.WhenAll(service.RedeemAsync(issued.Secret, RandomNumberGenerator.GetBytes(32), token),
            service.RedeemAsync(issued.Secret, RandomNumberGenerator.GetBytes(32), token));
        Assert.AreEqual(1, results.Count(x => x.Status == AgentIdentityStatus.Success));
        Assert.AreEqual(1, results.Count(x => x.Status == AgentIdentityStatus.Invalid));
        await using var check = agents.CreateDbContext();
        Assert.AreEqual(1, await check.Agents.CountAsync(token));
    });

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ArchiveHoldingDeviceLockPreventsConcurrentEnrollment(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await service.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        await using var archive = resources.CreateDbContext();
        await using var transaction = await archive.Database.BeginTransactionAsync(token);
        var current = await archive.Devices.SingleAsync(x => x.Id == device.Id, token);
        current.Archive(clock.GetUtcNow());
        await archive.SaveChangesAsync(token);
        var redeem = service.RedeemAsync(issued.Secret, RandomNumberGenerator.GetBytes(32), token);
        // Verify the competing operation is actually waiting for SQL, rather than merely scheduled.
        await Task.Delay(250, token);
        Assert.IsFalse(redeem.IsCompleted, "Enrollment must wait until the archive transaction commits.");
        await transaction.CommitAsync(token);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await redeem).Status);
        await using var check = agents.CreateDbContext();
        Assert.AreEqual(0, await check.Agents.CountAsync(token));
    }, snapshotReads);

    [TestMethod]
    public Task RecoveryRequiresBothSecretsAndNeverCreatesARegistration() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var actor = Guid.CreateVersion7();
        var issued = await service.IssueAsync(device.Id, actor, token);
        var machine = RandomNumberGenerator.GetBytes(32);
        var proof = $"vlr_{issued.Secret!.Substring(4, 36)}.{Convert.ToBase64String(machine).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RecoverAsync(issued.Secret, proof, token)).Status);
        var enrolled = await service.RedeemAsync(issued.Secret, SHA256.HashData(machine), token);
        clock.Now = clock.Now.AddMinutes(16);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RedeemAsync(issued.Secret, SHA256.HashData(machine), token)).Status);
        Assert.AreEqual(enrolled.AgentId, (await service.RecoverAsync(issued.Secret, proof, token)).AgentId);
        var wrong = RandomNumberGenerator.GetBytes(32);
        var wrongProof = $"vlr_{issued.Secret.Substring(4, 36)}.{Convert.ToBase64String(wrong).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RecoverAsync(issued.Secret, wrongProof, token)).Status);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RecoverAsync(null, proof, token)).Status);
        await service.RevokeAsync(enrolled.AgentId!.Value, actor, token);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RecoverAsync(issued.Secret, proof, token)).Status);
        await using var check = agents.CreateDbContext();
        Assert.AreEqual(1, await check.Agents.CountAsync(token));
    });

    [TestMethod]
    public Task RotationSurvivesRetriesAndDoesNotActivatePendingCredentialEarly() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await service.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var oldSecret = RandomNumberGenerator.GetBytes(32);
        var enrolled = await service.RedeemAsync(issued.Secret, SHA256.HashData(oldSecret), token);
        var newSecret = RandomNumberGenerator.GetBytes(32);
        var oldCredential = Credential(enrolled.AgentId!.Value, oldSecret);
        var newCredential = Credential(enrolled.AgentId.Value, newSecret);
        Assert.AreEqual(AgentIdentityStatus.Success, await service.BeginRotationAsync(oldCredential, SHA256.HashData(newSecret), token));
        Assert.AreEqual(AgentIdentityStatus.Success, await service.BeginRotationAsync(oldCredential, SHA256.HashData(newSecret), token));
        Assert.AreEqual(AgentIdentityStatus.Conflict, await service.BeginRotationAsync(oldCredential, RandomNumberGenerator.GetBytes(32), token));
        Assert.IsNull(await service.AuthenticateAsync(newCredential, token));
        Assert.AreEqual(AgentIdentityStatus.Invalid, await service.FinishRotationAsync(oldCredential, token));
        Assert.AreEqual(AgentIdentityStatus.Success, await service.FinishRotationAsync(newCredential, token));
        Assert.AreEqual(AgentIdentityStatus.Success, await service.FinishRotationAsync(newCredential, token));
        Assert.IsNull(await service.AuthenticateAsync(oldCredential, token));
        Assert.IsNotNull(await service.AuthenticateAsync(newCredential, token));
        var pending = RandomNumberGenerator.GetBytes(32);
        Assert.AreEqual(AgentIdentityStatus.Success, await service.BeginRotationAsync(newCredential, SHA256.HashData(pending), token));
        clock.Now = clock.Now.AddMinutes(16);
        Assert.AreEqual(AgentIdentityStatus.Invalid, await service.FinishRotationAsync(Credential(enrolled.AgentId.Value, pending), token));
        Assert.IsNotNull(await service.AuthenticateAsync(newCredential, token));
    });

    [TestMethod]
    public Task ExpiredRevokedAndIneligibleRegistrationsAreRejected() => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("server", "127.0.0.1", true, clock.GetUtcNow());
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var service = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var actor = Guid.CreateVersion7();
        var first = await service.IssueAsync(device.Id, actor, token);
        var second = await service.IssueAsync(device.Id, actor, token);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RedeemAsync(first.Secret, RandomNumberGenerator.GetBytes(32), token)).Status);
        clock.Now = clock.Now.AddMinutes(16);
        Assert.AreEqual(AgentIdentityStatus.Invalid, (await service.RedeemAsync(second.Secret, RandomNumberGenerator.GetBytes(32), token)).Status);
        var third = await service.IssueAsync(device.Id, actor, token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var enrolled = await service.RedeemAsync(third.Secret, SHA256.HashData(secret), token);
        Assert.AreEqual(AgentIdentityStatus.Success, enrolled.Status);
        Assert.AreEqual(AgentIdentityStatus.Success, await service.RevokeAsync(enrolled.AgentId!.Value, actor, token));
        var credential = $"vla_{enrolled.AgentId:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        Assert.IsNull(await service.AuthenticateAsync(credential, token));
        await using (var db = resources.CreateDbContext())
        {
            var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            current.Update(current.Name, current.Address, false, true, clock.GetUtcNow()); await db.SaveChangesAsync(token);
        }
        Assert.AreEqual(AgentIdentityStatus.Conflict, (await service.IssueAsync(device.Id, actor, token)).Status);
    });

    private Task WithDatabase(Func<Factory<ResourcesDbContext>, Factory<AgentManagementDbContext>, CancellationToken, Task> test, bool snapshotReads = false) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var resources = new Factory<ResourcesDbContext>(() => new ResourcesDbContext(new DbContextOptionsBuilder<ResourcesDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options));
            var agents = new Factory<AgentManagementDbContext>(() => new AgentManagementDbContext(new DbContextOptionsBuilder<AgentManagementDbContext>()
                .UseSqlServer(database.ConnectionString, sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options));
            if (snapshotReads)
            {
                var server = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString);
                var name = server.InitialCatalog; server.InitialCatalog = "master";
                server.Pooling = false;
                await using var connection = new Microsoft.Data.SqlClient.SqlConnection(server.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON";
                await command.ExecuteNonQueryAsync(token);
            }
            await using (var db = resources.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = agents.CreateDbContext()) await db.Database.MigrateAsync(token);
            await test(resources, agents, token);
        }, TestContext.CancellationToken);

    private sealed class Factory<T>(Func<T> create) : IDbContextFactory<T> where T : DbContext { public T CreateDbContext() => create(); }
    private sealed class TestClock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private static string Credential(Guid id, byte[] secret) => $"vla_{id:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
}

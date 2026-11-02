using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
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
    public TestContext TestContext { get; set; } = null!;
    [AssemblyCleanup] public static void Cleanup() => SqlTestDatabase.CompleteRun();

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task Batches_AreAtomicIdempotentAndRejectChangedMessages(bool snapshotReads) => WithDatabase(async (resources, agents, token) =>
    {
        var clock = new TestClock();
        var device = new Device("batch server", "127.0.0.1", true, clock.Now);
        await using (var db = resources.CreateDbContext()) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var identity = new AgentIdentity(agents, new DeviceAgentSource(resources), clock);
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var secret = RandomNumberGenerator.GetBytes(32);
        var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
        var credential = Credential(registered.AgentId!.Value, secret);
        var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, clock.Now, "Windows test", "X64", ".NET 10");
        var other = item with { MessageId = Guid.CreateVersion7(), OperatingSystem = "Linux test" };
        var batch = new AgentBatch(1, Guid.CreateVersion7(), clock.Now, [item, other]);
        var payload = AgentBatchCodec.Serialize(batch);
        var receiver = new AgentBatchReceiver(agents, identity, clock);
        var results = await Task.WhenAll(receiver.ReceiveAsync(credential, payload, token), receiver.ReceiveAsync(credential, payload, token));
        Assert.AreEqual(1, results.Count(x => x == BatchReceiptStatus.Accepted));
        Assert.AreEqual(1, results.Count(x => x == BatchReceiptStatus.AlreadyAccepted));
        Assert.AreEqual(BatchReceiptStatus.Conflict, await receiver.ReceiveAsync(credential,
            AgentBatchCodec.Serialize(batch with { Items = [item with { RuntimeVersion = "different" }, other] }), token));
        var anotherBatch = batch with { BatchId = Guid.CreateVersion7(), Items = [item] };
        Assert.AreEqual(BatchReceiptStatus.Accepted, await receiver.ReceiveAsync(credential, AgentBatchCodec.Serialize(anotherBatch), token));
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
    }, snapshotReads);

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
    });

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
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 2, queue), token));
        var queueStatus = (await monitoring.GetAsync(device.Id, token))!.Queue;
        Assert.AreEqual(receivedAt, queueStatus!.ReportedAt);
        Assert.AreEqual(queue, queueStatus.Snapshot);
        clock.Now = receivedAt.AddSeconds(1);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 1, null), token));
        Assert.AreEqual(queue, (await monitoring.GetAsync(device.Id, token))!.Queue!.Snapshot, "A late older sequence must not replace newer queue status.");
        clock.Now = receivedAt.AddSeconds(-1);
        Assert.AreEqual(AgentIdentityStatus.Success, await identity.RecordHeartbeatAsync(credential, new AgentHeartbeatReport(1, epoch, 3, null), token));
        queueStatus = (await monitoring.GetAsync(device.Id, token))!.Queue;
        Assert.AreEqual(clock.Now, queueStatus!.ReportedAt);
        Assert.IsNull(queueStatus.Snapshot, "Unavailable must differ from an empty queue.");
        await Assert.ThrowsAsync<ArgumentException>(() => identity.RecordHeartbeatAsync(credential,
            new AgentHeartbeatReport(1, epoch, 4, queue with { QuarantinedBatches = 4 }), token));
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
            await using (var db = resources.CreateDbContext()) await db.Database.MigrateAsync(token);
            await using (var db = agents.CreateDbContext()) await db.Database.MigrateAsync(token);
            if (snapshotReads)
            {
                var server = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(database.ConnectionString);
                var name = server.InitialCatalog; server.InitialCatalog = "master";
                await using var connection = new Microsoft.Data.SqlClient.SqlConnection(server.ConnectionString);
                await connection.OpenAsync(token);
                await using var command = connection.CreateCommand();
                command.CommandText = $"ALTER DATABASE [{name}] SET READ_COMMITTED_SNAPSHOT ON";
                await command.ExecuteNonQueryAsync(token);
            }
            await test(resources, agents, token);
        }, TestContext.CancellationToken);

    private sealed class Factory<T>(Func<T> create) : IDbContextFactory<T> where T : DbContext { public T CreateDbContext() => create(); }
    private sealed class TestClock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private static string Credential(Guid id, byte[] secret) => $"vla_{id:D}.{Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
}

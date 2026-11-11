using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Contracts.Agent.Inventory;
using Microsoft.Data.Sqlite;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class InventoryGenerationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LogSchema_InitializedDuringInventoryFenceRestoresVersionThree()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryGenerationTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var agentId = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = agentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var owner = new AgentSettingsOwner(agentId, "https://example.test/", vault.StateDirectory);
            var queue = new DurableBatchQueue(vault, agentId);
            var now = DateTimeOffset.UtcNow;
            var snapshot = Guid.CreateVersion7();
            var batches = InventoryChunkBuilder.Build(snapshot, 1, InventorySource.LinuxDpkg, now, now, InventoryOutcome.Complete, []);
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueInventoryForOwnerAsync(batches, owner, InventorySource.LinuxDpkg, 1, snapshot, token));
            var logSource = Guid.CreateVersion7();
            var logGeneration = Guid.CreateVersion7();
            Assert.IsTrue(await queue.BindLogSourceAsync(owner, logSource, null, logGeneration, false, token));
            Assert.AreEqual(new LogCursor(logGeneration, null), await queue.ReadLogCursorAsync(owner, logSource, token));
            var status = new InventoryStatusResult(InventoryStatusResultKind.Success,
                [new(InventorySource.LinuxDpkg, 1, null, 1, InventoryOutcome.Complete, InventoryCompletion.Complete, null, snapshot)], owner);
            Assert.AreEqual(InventoryReconcileResult.Completed, await queue.ReconcileInventoryAsync(owner,
                new(InventorySource.LinuxDpkg, 1, snapshot), status, token));
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString());
            await db.OpenAsync(token);
            await using var command = db.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Assert.AreEqual(3L, (long)(await command.ExecuteScalarAsync(token))!);
            Assert.AreEqual(new LogCursor(logGeneration, null), await queue.ReadLogCursorAsync(owner, logSource, token));
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task EnqueueInventory_PersistsGenerationAndAllBatchesAtomicallyAcrossRestart()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryGenerationTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var agentId = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = agentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var owner = new AgentSettingsOwner(agentId, "https://example.test/", vault.StateDirectory);
            var now = DateTimeOffset.UtcNow;
            var snapshot = Guid.CreateVersion7();
            var entries = Enumerable.Range(0, 101).Select(i => new InventoryEntry($"p{i}", $"p{i}", "1", "x64")).ToArray();
            var batches = InventoryChunkBuilder.Build(snapshot, 2, InventorySource.LinuxDpkg, now, now, InventoryOutcome.Complete, entries);
            Assert.HasCount(2, batches);
            var limited = new DurableBatchQueue(vault, agentId, 1, 10);
            Assert.AreEqual(EnqueueResult.ExceedsCapacity,
                await limited.EnqueueInventoryForOwnerAsync(batches, owner, InventorySource.LinuxDpkg, 2, snapshot, token));
            Assert.IsNull(await limited.ReadOutstandingInventoryAsync(owner, token));
            var queue = new DurableBatchQueue(vault, agentId);
            using (var failing = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                await failing.OpenAsync(token);
                using var setup = failing.CreateCommand();
                setup.CommandText = "CREATE TRIGGER test_inventory_failure BEFORE INSERT ON inventory_outstanding_batches BEGIN SELECT RAISE(ABORT, 'injected failure'); END";
                await setup.ExecuteNonQueryAsync(token);
                await Assert.ThrowsAsync<SqliteException>(() => queue.EnqueueInventoryForOwnerAsync(batches, owner, InventorySource.LinuxDpkg, 2, snapshot, token));
                Assert.AreEqual(0L, (await queue.GetStatusAsync(token)).TotalBatches);
                Assert.IsNull(await queue.ReadOutstandingInventoryAsync(owner, token));
                setup.CommandText = "PRAGMA user_version";
                Assert.AreEqual(2L, (long)(await setup.ExecuteScalarAsync(token))!);
                setup.CommandText = "DROP TRIGGER test_inventory_failure";
                await setup.ExecuteNonQueryAsync(token);
            }
            Assert.AreEqual(EnqueueResult.Queued,
                await queue.EnqueueInventoryForOwnerAsync(batches, owner, InventorySource.LinuxDpkg, 2, snapshot, token));
            var reopened = new DurableBatchQueue(new IdentityVault(root), agentId);
            async Task<long> QueueVersion()
            {
                await using var versionDb = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString());
                await versionDb.OpenAsync(token);
                await using var query = versionDb.CreateCommand();
                query.CommandText = "PRAGMA user_version";
                return (long)(await query.ExecuteScalarAsync(token))!;
            }
            Assert.AreEqual(4L, await QueueVersion());
            Assert.AreEqual(new InventoryOutstanding(InventorySource.LinuxDpkg, 2, snapshot), await reopened.ReadOutstandingInventoryAsync(owner, token));
            Assert.AreEqual(2L, (await reopened.GetStatusAsync(token)).TotalBatches);
            Assert.AreEqual(EnqueueResult.AlreadyQueued,
                await reopened.EnqueueInventoryForOwnerAsync(batches, owner, InventorySource.LinuxDpkg, 2, snapshot, token));
            var conflicting = batches.ToArray();
            Assert.IsTrue(InventoryChunkCodec.TryRead(conflicting[0].Payload, out var chunk));
            var changed = chunk! with { BatchId = Guid.CreateVersion7() };
            conflicting[0] = conflicting[0] with { BatchId = changed.BatchId, Payload = InventoryChunkCodec.Serialize(changed) };
            Assert.AreEqual(EnqueueResult.ConflictingBatch,
                await reopened.EnqueueInventoryForOwnerAsync(conflicting, owner, InventorySource.LinuxDpkg, 2, snapshot, token));
            var nextSnapshot = Guid.CreateVersion7();
            var next = InventoryChunkBuilder.Build(nextSnapshot, 3, InventorySource.LinuxDpkg, now, now, InventoryOutcome.Complete, []);
            Assert.AreEqual(EnqueueResult.InventoryOutstanding,
                await reopened.EnqueueInventoryForOwnerAsync(next, owner, InventorySource.LinuxDpkg, 3, nextSnapshot, token));
            var outstanding = new InventoryOutstanding(InventorySource.LinuxDpkg, 2, snapshot);
            var pendingStatus = new InventoryStatusResult(InventoryStatusResultKind.Success,
                [new(InventorySource.LinuxDpkg, 2, 2, null, null, null, null)], owner);
            Assert.AreEqual(InventoryReconcileResult.Pending, await reopened.ReconcileInventoryAsync(owner, outstanding, pendingStatus, token));
            var completed = new InventoryStatusResult(InventoryStatusResultKind.Success,
                [new(InventorySource.LinuxDpkg, 2, null, 2, InventoryOutcome.Complete, InventoryCompletion.Complete, null, snapshot)], owner);
            var acknowledgement = queue.AcknowledgeAsync(batches[0].BatchId, token);
            var reconciliation = reopened.ReconcileInventoryAsync(owner, outstanding, completed, token);
            await Task.WhenAll(acknowledgement, reconciliation);
            Assert.AreEqual(InventoryReconcileResult.Completed, await reconciliation);
            Assert.IsNull(await reopened.ReadOutstandingInventoryAsync(owner, token));
            Assert.AreEqual(0L, (await reopened.GetStatusAsync(token)).TotalBatches);
            Assert.AreEqual(0L, (await reopened.GetStatusAsync(token)).TotalBytes);
            Assert.AreEqual(2L, await QueueVersion());
            Assert.AreEqual(EnqueueResult.Queued,
                await reopened.EnqueueInventoryForOwnerAsync(next, owner, InventorySource.LinuxDpkg, 3, nextSnapshot, token));
            var rejected = new InventoryStatusResult(InventoryStatusResultKind.Success,
                [new(InventorySource.LinuxDpkg, 3, null, 3, InventoryOutcome.Complete, InventoryCompletion.Rejected, InventoryRejectionReason.Capacity, nextSnapshot)], owner);
            Assert.AreEqual(InventoryReconcileResult.Rejected, await reopened.ReconcileInventoryAsync(owner,
                new(InventorySource.LinuxDpkg, 3, nextSnapshot), rejected, token));
            Assert.IsNull(await reopened.ReadOutstandingInventoryAsync(owner, token));
            Assert.AreEqual(1L, (await reopened.GetStatusAsync(token)).QuarantinedBatches);
            Assert.AreEqual(4L, await QueueVersion());
            var fourthId = Guid.CreateVersion7();
            var fourth = InventoryChunkBuilder.Build(fourthId, 4, InventorySource.LinuxDpkg, now, now, InventoryOutcome.Complete, []);
            Assert.AreEqual(EnqueueResult.Queued,
                await reopened.EnqueueInventoryForOwnerAsync(fourth, owner, InventorySource.LinuxDpkg, 4, fourthId, token));
            var newerRejection = new InventoryStatusResult(InventoryStatusResultKind.Success,
                [new(InventorySource.LinuxDpkg, 5, null, 5, InventoryOutcome.Complete, InventoryCompletion.Rejected, InventoryRejectionReason.Capacity)], owner);
            Assert.AreEqual(InventoryReconcileResult.Rejected, await reopened.ReconcileInventoryAsync(owner,
                new(InventorySource.LinuxDpkg, 4, fourthId), newerRejection, token));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString());
            await db.OpenAsync(token);
            using var command = db.CreateCommand();
            command.CommandText = "SELECT rejection FROM batches WHERE batch_id = $id";
            command.Parameters.AddWithValue("$id", fourth[0].BatchId.ToString("D"));
            Assert.AreEqual((long)QueueRejection.Expired, await command.ExecuteScalarAsync(token));
            await db.CloseAsync();
            Assert.IsTrue(await reopened.AcknowledgeAsync(next[0].BatchId, token));
            Assert.AreEqual(4L, await QueueVersion());
            Assert.IsTrue(await reopened.AcknowledgeAsync(fourth[0].BatchId, token));
            Assert.AreEqual(2L, await QueueVersion());
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Reserve_PersistsPerSourceAndSerializesConcurrentOwners()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryGenerationTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var agentId = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = agentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var owner = new AgentSettingsOwner(agentId, "https://example.test/", vault.StateDirectory);
            var queues = Enumerable.Range(0, 6).Select(_ => new DurableBatchQueue(vault, agentId)).ToArray();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            async Task<long> ReserveWithRetry(DurableBatchQueue queue)
            {
                while (true)
                {
                    try { return await queue.ReserveInventoryGenerationAsync(owner, InventorySource.LinuxRpm, 0, deadline.Token); }
                    catch (IOException ex) when (!deadline.IsCancellationRequested &&
                        (OperatingSystem.IsWindows() && (ex.HResult & 0xffff) is 32 or 33 ||
                         OperatingSystem.IsLinux() && (ex.HResult & 0xffff) == 11))
                    { await Task.Delay(50, deadline.Token); }
                }
            }
            // The identity lease has a bounded wait; a busy producer retries without reusing a generation.
            var values = await Task.WhenAll(queues.Select(ReserveWithRetry));
            CollectionAssert.AreEquivalent(new long[] { 1, 2, 3, 4, 5, 6 }, values);
            var reopened = new DurableBatchQueue(new IdentityVault(root), agentId);
            Assert.AreEqual(7L, await reopened.ReserveInventoryGenerationAsync(owner, InventorySource.LinuxRpm, 0, token));
            Assert.AreEqual(51L, await reopened.ReserveInventoryGenerationAsync(owner, InventorySource.LinuxRpm, 50, token));
            Assert.AreEqual(1L, await reopened.ReserveInventoryGenerationAsync(owner, InventorySource.LinuxDpkg, 0, token));
            var wrongOwner = owner with { AgentId = Guid.CreateVersion7() };
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReserveInventoryGenerationAsync(wrongOwner, InventorySource.LinuxRpm, 0, token));
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync(token);
                using var lost = connection.CreateCommand();
                lost.CommandText = "DROP TABLE inventory_generations";
                await lost.ExecuteNonQueryAsync(token);
            }
            Assert.AreEqual(101L, await reopened.ReserveInventoryGenerationAsync(owner, InventorySource.LinuxRpm, 100, token));
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                await connection.OpenAsync(token);
                using var corrupt = connection.CreateCommand();
                corrupt.CommandText = "PRAGMA ignore_check_constraints = ON; UPDATE inventory_generations SET generation = 'invalid' WHERE source = 4";
                await corrupt.ExecuteNonQueryAsync(token);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ReserveInventoryGenerationAsync(owner, InventorySource.LinuxRpm, 0, token));
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

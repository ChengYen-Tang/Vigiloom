using Microsoft.Data.Sqlite;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class DurableBatchQueueTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SqliteNativeLibrary_LoadsPinnedVersion()
    {
        using var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT sqlite_version()";
        var version = Version.Parse((string)command.ExecuteScalar()!);
        Assert.AreEqual(new Version(3, 53, 3), version);
    }

    [TestMethod]
    public async Task Queue_ReopensWithSameBytesAndOrderUntilAcknowledged()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var time = DateTimeOffset.UtcNow;
            var firstId = Guid.CreateVersion7();
            var secondId = Guid.CreateVersion7();
            var owner = Guid.CreateVersion7();
            var queue = new DurableBatchQueue(new IdentityVault(root), owner, 8);
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(firstId, time, 2, new byte[] { 1, 2, 3 }, token));
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(secondId, time, 1, new byte[] { 4, 5 }, token));
            var reopened = new DurableBatchQueue(new IdentityVault(root), owner, 8);
            var first = await reopened.PeekAsync(token);
            Assert.IsNotNull(first);
            Assert.AreEqual(firstId, first.BatchId);
            Assert.AreEqual(time, first.EnqueuedAt);
            Assert.AreEqual(2, first.RecordCount);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, first.Payload);
            Assert.AreEqual(EnqueueResult.AlreadyQueued, await reopened.EnqueueAsync(firstId, time, 2, first.Payload, token));
            Assert.AreEqual(EnqueueResult.ConflictingBatch, await reopened.EnqueueAsync(firstId, time, 2, new byte[] { 1, 2, 9 }, token));
            Assert.AreEqual(EnqueueResult.Full, await reopened.EnqueueAsync(Guid.CreateVersion7(), time, 1, new byte[4], token));
            var differentAgent = new DurableBatchQueue(new IdentityVault(root), Guid.CreateVersion7(), 8);
            await Assert.ThrowsAsync<InvalidOperationException>(() => differentAgent.PeekAsync(token));
            Assert.IsFalse(await reopened.AcknowledgeAsync(Guid.CreateVersion7(), token));
            Assert.IsTrue(await reopened.AcknowledgeAsync(firstId, token));
            var thirdId = Guid.CreateVersion7();
            Assert.AreEqual(EnqueueResult.Queued, await reopened.EnqueueAsync(thirdId, time, 1, new byte[4], token));
            Assert.AreEqual(secondId, (await reopened.PeekAsync(token))!.BatchId);
            Assert.IsTrue(await reopened.AcknowledgeAsync(secondId, token));
            Assert.AreEqual(thirdId, (await reopened.PeekAsync(token))!.BatchId);
            Assert.IsTrue(await reopened.AcknowledgeAsync(thirdId, token));
            Assert.IsNull(await reopened.PeekAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_AcceptsBacklogBeyondFormerCountLimit()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var owner = Guid.CreateVersion7();
            var queue = new DurableBatchQueue(new IdentityVault(root), owner);
            var now = DateTimeOffset.UtcNow;
            await queue.EnqueueAsync(Guid.CreateVersion7(), now, 1, new byte[] { 1 }, token);
            // Seed a large persisted backlog in one transaction so this integration
            // test exercises reopening and the old boundary without 8,192 fsyncs.
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var transaction = db.BeginTransaction();
                using var command = db.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    WITH RECURSIVE numbers(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM numbers WHERE n < 8192)
                    INSERT INTO batches(batch_id, created_ticks, record_count, payload)
                    SELECT printf('%08x-0000-7000-8000-000000000000', n), $ticks, 1, X'01' FROM numbers;
                    UPDATE queue_state SET queued_bytes = queued_bytes + 8192, queued_count = queued_count + 8192 WHERE singleton = 1;
                    """;
                command.Parameters.AddWithValue("$ticks", now.UtcTicks);
                command.ExecuteNonQuery();
                transaction.Commit();
            }
            var reopened = new DurableBatchQueue(new IdentityVault(root), owner);
            Assert.AreEqual(EnqueueResult.Queued, await reopened.EnqueueAsync(Guid.CreateVersion7(), now, 1, new byte[] { 2 }, token));
            Assert.AreEqual(new QueueStatus(8194, 8194, 0), await reopened.GetStatusAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_BatchCountLimitAndCancellationDoNotRemovePendingData()
    {
        var root = NewRoot();
        try
        {
            var queue = new DurableBatchQueue(new IdentityVault(root), Guid.CreateVersion7(), 100, maximumBatches: 2);
            var id = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var token = TestContext.CancellationToken;
            await queue.EnqueueAsync(id, now, 1, new byte[] { 1 }, token);
            await queue.EnqueueAsync(Guid.CreateVersion7(), now, 1, new byte[] { 2 }, token);
            Assert.AreEqual(EnqueueResult.Full, await queue.EnqueueAsync(Guid.CreateVersion7(), now, 1, new byte[] { 3 }, token));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => queue.AcknowledgeAsync(id, cancelled.Token));
            Assert.AreEqual(id, (await queue.PeekAsync(token))!.BatchId);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_FailedMetadataUpdateRollsBackBatchAndCanRetry()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var owner = Guid.CreateVersion7();
            var queue = new DurableBatchQueue(new IdentityVault(root), owner, 100, maximumBatches: 10);
            var now = DateTimeOffset.UtcNow;
            var original = Guid.CreateVersion7();
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(original, now, 1, new byte[] { 1, 2 }, token));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                await db.OpenAsync(token);
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER test_reject_queue_update BEFORE UPDATE ON queue_state BEGIN SELECT RAISE(ABORT, 'injected metadata failure'); END";
                await command.ExecuteNonQueryAsync(token);
            }
            var next = Guid.CreateVersion7();
            await Assert.ThrowsAsync<SqliteException>(() => queue.EnqueueAsync(next, now, 1, new byte[] { 3, 4, 5 }, token));
            var reopened = new DurableBatchQueue(new IdentityVault(root), owner, 100, maximumBatches: 10);
            Assert.AreEqual(new QueueStatus(2, 1, 0), await reopened.GetStatusAsync(token));
            Assert.AreEqual(original, (await reopened.PeekAsync(token))!.BatchId);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                await db.OpenAsync(token);
                using var command = db.CreateCommand();
                command.CommandText = "DROP TRIGGER test_reject_queue_update";
                await command.ExecuteNonQueryAsync(token);
            }
            Assert.AreEqual(EnqueueResult.Queued, await reopened.EnqueueAsync(next, now, 1, new byte[] { 3, 4, 5 }, token));
            Assert.AreEqual(new QueueStatus(5, 2, 0), await reopened.GetStatusAsync(token));
            Assert.IsTrue(await reopened.AcknowledgeAsync(original, token));
            Assert.AreEqual(next, (await reopened.PeekAsync(token))!.BatchId);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_IdentityLeaseContentionDoesNotDiscardBatch()
    {
        var root = NewRoot();
        try
        {
            var vault = new IdentityVault(root);
            var queue = new DurableBatchQueue(vault, Guid.CreateVersion7());
            var token = TestContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            await using (var lease = vault.Acquire())
                await Assert.ThrowsAsync<IOException>(() => queue.EnqueueAsync(id, now, 1, new byte[] { 42 }, token));
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(id, now, 1, new byte[] { 42 }, token));
            Assert.AreEqual(id, (await queue.PeekAsync(token))!.BatchId);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_TemporaryIdentityLeaseContentionRetriesWithoutDiscardingBatch()
    {
        var root = NewRoot();
        try
        {
            var vault = new IdentityVault(root);
            var queue = new DurableBatchQueue(vault, Guid.CreateVersion7());
            var token = TestContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var session = vault.Acquire();
            Task<EnqueueResult> pending;
            try
            {
                pending = queue.EnqueueAsync(id, DateTimeOffset.UtcNow, 1, new byte[] { 42 }, token);
                Assert.IsFalse(pending.IsCompleted, "Queue operation must wait for a briefly held identity lease.");
            }
            finally { await session.DisposeAsync(); }
            Assert.AreEqual(EnqueueResult.Queued, await pending);
            Assert.AreEqual(id, (await queue.PeekAsync(token))!.BatchId);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_ConcurrentProducersAreSerializedAndCapacityIsNotExceeded()
    {
        var root = NewRoot();
        try
        {
            var queue = new DurableBatchQueue(new IdentityVault(root), Guid.CreateVersion7(), 100);
            var tasks = Enumerable.Range(0, 30).Select(_ => Task.Run(() => queue.EnqueueAsync(Guid.CreateVersion7(),
                DateTimeOffset.UtcNow, 1, new byte[10], TestContext.CancellationToken))).ToArray();
            var results = await Task.WhenAll(tasks);
            Assert.AreEqual(10, results.Count(x => x == EnqueueResult.Queued));
            Assert.AreEqual(20, results.Count(x => x == EnqueueResult.Full));
            for (var i = 0; i < 10; i++)
            {
                var batch = await queue.PeekAsync(TestContext.CancellationToken);
                Assert.IsNotNull(batch);
                Assert.IsTrue(await queue.AcknowledgeAsync(batch.BatchId, TestContext.CancellationToken));
            }
            Assert.IsNull(await queue.PeekAsync(TestContext.CancellationToken));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_QuarantinedBatchSurvivesReopenAndStillCountsAgainstCapacity()
    {
        var root = NewRoot();
        try
        {
            var owner = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            var token = TestContext.CancellationToken;
            var queue = new DurableBatchQueue(vault, owner, 4);
            var first = Guid.CreateVersion7();
            var second = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            await queue.EnqueueAsync(first, now, 1, new byte[] { 1, 2 }, token);
            await queue.EnqueueAsync(second, now, 1, new byte[] { 3, 4 }, token);
            Assert.IsTrue(await queue.QuarantineAsync(first, QueueRejection.Conflict, token));
            Assert.IsFalse(await queue.QuarantineAsync(first, QueueRejection.InvalidLocalBatch, token));
            var reopened = new DurableBatchQueue(vault, owner, 4);
            Assert.AreEqual(second, (await reopened.PeekAsync(token))!.BatchId);
            Assert.AreEqual(new QueueStatus(4, 2, 1), await reopened.GetStatusAsync(token));
            Assert.AreEqual(EnqueueResult.Full, await reopened.EnqueueAsync(Guid.CreateVersion7(), now, 1, new byte[] { 5 }, token));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString());
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT rejection, payload FROM batches WHERE batch_id = $id";
            command.Parameters.AddWithValue("$id", first.ToString("D"));
            using var reader = command.ExecuteReader();
            Assert.IsTrue(reader.Read());
            Assert.AreEqual((int)QueueRejection.Conflict, reader.GetInt32(0));
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, (byte[])reader[1]);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_ConcurrentAcknowledgmentAndQuarantinePreserveCounters()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var queue = new DurableBatchQueue(new IdentityVault(root), Guid.CreateVersion7());
            var id = Guid.CreateVersion7();
            await queue.EnqueueAsync(id, DateTimeOffset.UtcNow, 1, new byte[] { 1, 2 }, token);
            var quarantine = Task.Run(() => queue.QuarantineAsync(id, QueueRejection.Conflict, token), token);
            var acknowledgement = Task.Run(() => queue.AcknowledgeAsync(id, token), token);
            await Task.WhenAll(quarantine, acknowledgement);
            Assert.IsTrue(acknowledgement.Result, "A confirmed server receipt must remove its batch exactly once.");
            Assert.AreEqual(new QueueStatus(0, 0, 0), await queue.GetStatusAsync(token));
            Assert.IsNull(await queue.PeekAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_UpgradesVersionOneWithoutChangingPendingPayload()
    {
        var root = NewRoot();
        try
        {
            var owner = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            var token = TestContext.CancellationToken;
            var id = Guid.CreateVersion7();
            await new DurableBatchQueue(vault, owner).EnqueueAsync(id, DateTimeOffset.UtcNow, 1, new byte[] { 9, 8 }, token);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand(); command.CommandText = "ALTER TABLE batches DROP COLUMN rejection; PRAGMA user_version = 1"; command.ExecuteNonQuery();
            }
            var wrongOwner = new DurableBatchQueue(vault, Guid.CreateVersion7());
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrongOwner.PeekAsync(token));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
                Assert.AreEqual(1L, (long)command.ExecuteScalar()!);
            }
            var reopened = new DurableBatchQueue(vault, owner);
            var pending = await reopened.PeekAsync(token);
            Assert.AreEqual(id, pending!.BatchId);
            CollectionAssert.AreEqual(new byte[] { 9, 8 }, pending.Payload);
            Assert.AreEqual(new QueueStatus(2, 1, 0), await reopened.GetStatusAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_FailedUpgradeDoesNotChangeVersionOrDiscardBatch()
    {
        var root = NewRoot();
        try
        {
            var owner = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            var token = TestContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var queue = new DurableBatchQueue(vault, owner);
            await queue.EnqueueAsync(id, DateTimeOffset.UtcNow, 1, new byte[] { 42 }, token);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version = 1"; command.ExecuteNonQuery();
            }
            await Assert.ThrowsAsync<SqliteException>(() => queue.PeekAsync(token));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
                Assert.AreEqual(1L, (long)command.ExecuteScalar()!);
                command.CommandText = "SELECT payload FROM batches WHERE batch_id = $id";
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                CollectionAssert.AreEqual(new byte[] { 42 }, (byte[])command.ExecuteScalar()!);
            }
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_UnknownDatabaseVersionFailsWithoutOverwritingData()
    {
        var root = NewRoot();
        try
        {
            var queue = new DurableBatchQueue(new IdentityVault(root), Guid.CreateVersion7());
            var id = Guid.CreateVersion7();
            await queue.EnqueueAsync(id, DateTimeOffset.UtcNow, 1, new byte[] { 42 }, TestContext.CancellationToken);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version = 3"; command.ExecuteNonQuery();
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => queue.PeekAsync(TestContext.CancellationToken));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version = 2"; command.ExecuteNonQuery();
            }
            Assert.AreEqual(id, (await queue.PeekAsync(TestContext.CancellationToken))!.BatchId);
        }
        finally { Cleanup(root); }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "VigiloomQueueTests", Path.GetRandomFileName());

    private static void Cleanup(string root)
    {
        var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomQueueTests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Test cleanup escaped its temporary root.");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

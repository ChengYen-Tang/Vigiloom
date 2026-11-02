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
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version = 2"; command.ExecuteNonQuery();
            }
            await Assert.ThrowsAsync<InvalidDataException>(() => queue.PeekAsync(TestContext.CancellationToken));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version = 1"; command.ExecuteNonQuery();
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

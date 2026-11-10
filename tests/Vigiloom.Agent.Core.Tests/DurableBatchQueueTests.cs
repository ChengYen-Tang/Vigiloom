using Microsoft.Data.Sqlite;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Logs;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class DurableBatchQueueTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task LogCycle_DrainsTextBacklogInOrderedBatches()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var path = Path.Combine(root, "events.log");
            var expected = Enumerable.Range(0, 400).Select(i => $"event-{i:D4}\n").ToArray();
            await File.WriteAllTextAsync(path, string.Concat(expected), token);
            var source = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.TextFile,
                "events", path, true, 1, AgentTextLogMode.SingleLine);
            var snapshot = AgentLogSettingsSnapshot.Create([source]);
            var saved = await new AgentLogSettingsStore(vault, new Uri(owner.Server), id).SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 10);
            var cycle = new LogCollectionCycle(owner, queue, TimeProvider.System,
                _ => new TextFileLogReader(path, AgentTextLogMode.SingleLine, TimeProvider.System));
            var actual = new List<string>();
            for (var pass = 0; pass < 4; pass++)
            {
                var results = await cycle.RunAsync(snapshot, saved.SaveToken, () => true, token);
                Assert.AreEqual(100, results.Single().QueuedCount);
                var batch = await queue.PeekAsync(token);
                Assert.IsNotNull(batch);
                Assert.IsTrue(AgentLogBatchCodec.TryRead(batch.Payload, out var parsed));
                actual.AddRange(parsed!.Items.Select(item => item.Content));
                Assert.IsTrue(await queue.AcknowledgeAsync(batch.BatchId, token));
            }
            CollectionAssert.AreEqual(expected, actual);
            Assert.AreEqual(LogSourceCycleStatus.End, (await cycle.RunAsync(snapshot, saved.SaveToken, () => true, token)).Single().Status);
            TestContext.WriteLine("Drained 400 text records in four durable batches; ordering was preserved.");
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LogCycle_RespectsConfiguredBatchLimit()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var source = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.Journal, "system", "local", true, 1, null);
            var snapshot = AgentLogSettingsSnapshot.Create([source], 1);
            var saved = await new AgentLogSettingsStore(vault, new Uri(owner.Server), id).SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var cycle = new LogCollectionCycle(owner, queue, TimeProvider.System, _ => new RepeatingLogReader());
            var result = await cycle.RunAsync(snapshot, saved.SaveToken, () => true, token);
            Assert.AreEqual(1, result[0].QueuedCount);
            Assert.IsTrue(result[0].MayHaveMore);
            var batch = await queue.PeekAsync(token);
            Assert.IsNotNull(batch);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(batch.Payload, out var parsed));
            Assert.HasCount(1, parsed!.Items);
            var now = DateTimeOffset.UtcNow;
            var oversized = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now,
                source.SourceId, source.Kind, source.Name, source.ParserVersion,
                [new(Guid.CreateVersion7(), now, "first", null, null, null, null, null), new(Guid.CreateVersion7(), now, "second", null, null, null, null, null)]);
            var payload = AgentLogBatchCodec.Serialize(oversized);
            var rejected = await queue.EnqueueLogAsync(owner, source.SourceId, source.Generation, "1", "3",
                new QueuedBatch(oversized.BatchId, now, 2, payload), saved.SaveToken, token);
            Assert.AreEqual(LogEnqueueResult.SettingsChanged, rejected);
            Assert.AreEqual("1", (await queue.ReadLogCursorAsync(owner, source.SourceId, token))!.Position);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LogCycle_LargeRecordsSplitWithoutAdvancingPastFullQueue()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var source = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.Journal, "system", "local", true, 1, null);
            var snapshot = AgentLogSettingsSnapshot.Create([source]);
            var saved = await new AgentLogSettingsStore(vault, new Uri(owner.Server), id).SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var cycle = new LogCollectionCycle(owner, queue, TimeProvider.System, _ => new LargeLogReader());
            var first = await cycle.RunAsync(snapshot, saved.SaveToken, () => true, token);
            Assert.AreEqual(2, first[0].QueuedCount);
            Assert.IsTrue(first[0].MayHaveMore);
            Assert.AreEqual("2", (await queue.ReadLogCursorAsync(owner, source.SourceId, token))!.Position);
            var batch = await queue.PeekAsync(token);
            Assert.IsNotNull(batch);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(batch.Payload, out var decoded));
            Assert.HasCount(2, decoded!.Items);
            var blocked = await cycle.RunAsync(snapshot, saved.SaveToken, () => true, token);
            Assert.AreEqual(LogSourceCycleStatus.Full, blocked[0].Status);
            Assert.AreEqual("2", (await queue.ReadLogCursorAsync(owner, source.SourceId, token))!.Position);
            Assert.IsTrue(await queue.AcknowledgeAsync(batch.BatchId, token));
            var resumed = await cycle.RunAsync(snapshot, saved.SaveToken, () => true, token);
            Assert.AreEqual(1, resumed[0].QueuedCount);
            Assert.AreEqual("3", (await queue.ReadLogCursorAsync(owner, source.SourceId, token))!.Position);
        }
        finally { Cleanup(root); }
    }

    private sealed class LargeLogReader : ILogSourceReader
    {
        private readonly string content = new('\u0001', AgentLogBatchCodec.MaximumContentCharacters);
        public ValueTask<LogReadResult> ReadAsync(string? cursor, CancellationToken token)
        {
            var next = cursor is null ? 1 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture) + 1;
            return ValueTask.FromResult(next > 3 ? new LogReadResult(LogReadStatus.End) :
                new LogReadResult(LogReadStatus.Record, next.ToString(System.Globalization.CultureInfo.InvariantCulture), new LogObservation(null, content)));
        }
    }

    [TestMethod]
    public async Task LogCycle_TextFileGapAndFollowingRecordSurviveQueueRestart()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var path = Path.Combine(root, "source.log");
            await File.WriteAllBytesAsync(path, [0xff, (byte)'\n', (byte)'o', (byte)'k', (byte)'\n'], token);
            var source = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.TextFile,
                "file", path, true, 1, AgentTextLogMode.SingleLine);
            var snapshot = AgentLogSettingsSnapshot.Create([source]);
            var saved = await new AgentLogSettingsStore(vault, new Uri(owner.Server), id).SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var cycle = new LogCollectionCycle(owner, queue, TimeProvider.System,
                _ => new TextFileLogReader(path, AgentTextLogMode.SingleLine, TimeProvider.System));
            var first = await cycle.RunAsync(snapshot, saved.SaveToken, () => true, token);
            Assert.AreEqual(LogSourceCycleStatus.Queued, first[0].Status);
            Assert.AreEqual(2, first[0].QueuedCount);
            var gap = await queue.PeekAsync(token);
            Assert.IsNotNull(gap);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(gap.Payload, out var parsed));
            Assert.AreEqual(AgentLogGapReason.InvalidEncoding, parsed!.Items[0].Gap!.Reason);
            Assert.AreEqual("ok\n", parsed.Items[1].Content);
            Assert.IsTrue(await queue.AcknowledgeAsync(gap.BatchId, token));
            var reopened = new DurableBatchQueue(new IdentityVault(root), id, 1024 * 1024, 1);
            var resumed = new LogCollectionCycle(owner, reopened, TimeProvider.System,
                _ => new TextFileLogReader(path, AgentTextLogMode.SingleLine, TimeProvider.System));
            var second = await resumed.RunAsync(snapshot, saved.SaveToken, () => true, token);
            Assert.AreEqual(LogSourceCycleStatus.End, second[0].Status);
            Assert.AreEqual(0, second[0].QueuedCount);
            Assert.IsNull(await reopened.PeekAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LogCycle_RotatesSourcesWhenQueueHasOnlyOneSlot()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var sources = Enumerable.Range(0, 2).Select(i => new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(),
                AgentLogSourceKind.Journal, $"source-{i}", "local", true, 1, null)).ToArray();
            var snapshot = AgentLogSettingsSnapshot.Create(sources);
            var saved = await new AgentLogSettingsStore(vault, new Uri(owner.Server), id).SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            var cycle = new LogCollectionCycle(owner, queue, TimeProvider.System, _ => new RepeatingLogReader());
            var first = await cycle.RunAsync(snapshot, saved.SaveToken, () => true, token);
            Assert.AreEqual(LogSourceCycleStatus.Full, first[1].Status);
            Assert.AreEqual(100, first[0].QueuedCount);
            Assert.IsTrue(first[0].MayHaveMore);
            Assert.IsNull((await queue.ReadLogCursorAsync(owner, first[1].SourceId, token))!.Position);
            var batch = await queue.PeekAsync(token);
            Assert.IsNotNull(batch);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(batch.Payload, out var parsedFirst));
            Assert.HasCount(100, parsedFirst!.Items);
            Assert.IsTrue(await queue.AcknowledgeAsync(batch.BatchId, token));
            var second = await cycle.RunAsync(snapshot, saved.SaveToken, () => true, token);
            Assert.AreEqual(100, second[0].QueuedCount);
            var next = await queue.PeekAsync(token);
            Assert.IsNotNull(next);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(next.Payload, out var parsedSecond));
            Assert.AreNotEqual(parsedFirst!.SourceId, parsedSecond!.SourceId);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LogCycle_StopsBeforeEnqueueWhenSettingsChangeDuringRead()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var source = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.Journal, "system", "local", true, 1, null);
            var current = true;
            var cycle = new LogCollectionCycle(owner, queue, TimeProvider.System, _ => new ChangingLogReader(() => current = false));
            var snapshot = AgentLogSettingsSnapshot.Create([source]);
            var saved = await new AgentLogSettingsStore(vault, new Uri(owner.Server), id).SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            var results = await cycle.RunAsync(snapshot, saved.SaveToken, () => current, token);
            Assert.AreEqual(LogSourceCycleStatus.SettingsChanged, results[0].Status);
            Assert.AreEqual(0, results[0].QueuedCount);
            Assert.IsNull(await queue.PeekAsync(token));
            Assert.IsNull((await queue.ReadLogCursorAsync(owner, source.SourceId, token))!.Position);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LogCycle_UnavailableSourceDoesNotBlockOtherSourcesOrAdvanceItsCursor()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var first = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.Journal, "one", "local", true, 1, null);
            var second = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.Journal, "two", "local", true, 1, null);
            var snapshot = AgentLogSettingsSnapshot.Create([first, second]);
            var saved = await new AgentLogSettingsStore(vault, new Uri(owner.Server), id).SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            var unavailable = snapshot.Sources[0].SourceId;
            var cycle = new LogCollectionCycle(owner, queue, TimeProvider.System,
                setting => setting.SourceId == unavailable ? new UnavailableLogReader() : new RepeatingLogReader());
            var results = await cycle.RunAsync(snapshot, saved.SaveToken, () => true, token);
            Assert.AreEqual(LogSourceCycleStatus.Unavailable, results[0].Status);
            Assert.AreEqual(100, results[1].QueuedCount);
            Assert.IsNull((await queue.ReadLogCursorAsync(owner, unavailable, token))!.Position);
            var queued = await queue.PeekAsync(token);
            Assert.IsNotNull(queued);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(queued.Payload, out var parsed));
            Assert.AreNotEqual(unavailable, parsed!.SourceId);
        }
        finally { Cleanup(root); }
    }

    private sealed class UnavailableLogReader : ILogSourceReader
    {
        public ValueTask<LogReadResult> ReadAsync(string? cursor, CancellationToken token) => throw new IOException("Unavailable");
    }

    private sealed class RepeatingLogReader : ILogSourceReader
    {
        public ValueTask<LogReadResult> ReadAsync(string? cursor, CancellationToken token) => ValueTask.FromResult(
            new LogReadResult(LogReadStatus.Record, (cursor is null ? 1 : int.Parse(cursor, System.Globalization.CultureInfo.InvariantCulture) + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture), new LogObservation(null, "event")));
    }

    private sealed class ChangingLogReader(Action change) : ILogSourceReader
    {
        public ValueTask<LogReadResult> ReadAsync(string? cursor, CancellationToken token)
        {
            change();
            return ValueTask.FromResult(new LogReadResult(LogReadStatus.Record, "next", new LogObservation(null, "event")));
        }
    }

    [TestMethod]
    public async Task LogCycle_RejectsOldReadAfterNewSettingsAreDurablySaved()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var source = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.Journal, "system", "local", true, 1, null);
            var snapshot = AgentLogSettingsSnapshot.Create([source]);
            var store = new AgentLogSettingsStore(vault, new Uri(owner.Server), id);
            var saved = await store.SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            var cycle = new LogCollectionCycle(owner, queue, TimeProvider.System, _ => new AsyncChangingLogReader(async () =>
            {
                var changed = AgentLogSettingsSnapshot.Create([source with { Enabled = false, Generation = Guid.CreateVersion7() }]);
                await store.SaveAsync(changed, saved.SaveToken, DateTimeOffset.UtcNow, token);
            }));
            // The runtime signal is deliberately stale; the durable fence must still reject the old work.
            var result = await cycle.RunAsync(snapshot, saved.SaveToken, () => true, token);
            Assert.AreEqual(LogSourceCycleStatus.SettingsChanged, result[0].Status);
            Assert.IsNull(await queue.PeekAsync(token));
            Assert.IsNull((await queue.ReadLogCursorAsync(owner, source.SourceId, token))!.Position);
        }
        finally { Cleanup(root); }
    }

    private sealed class AsyncChangingLogReader(Func<Task> change) : ILogSourceReader
    {
        public async ValueTask<LogReadResult> ReadAsync(string? cursor, CancellationToken token)
        {
            if (cursor is not null) return new(LogReadStatus.End);
            await change();
            return new(LogReadStatus.Record, "next", new LogObservation(null, "event"));
        }
    }

    [TestMethod]
    public async Task LogProducerOwnership_OnlyOneProducerCanOwnStateDirectory()
    {
        var root = NewRoot();
        try
        {
            var first = new IdentityVault(root);
            var second = new IdentityVault(root);
            await using (first.AcquireLogOwnership())
                Assert.ThrowsExactly<IOException>(() => second.AcquireLogOwnership());
            await using var reacquired = second.AcquireLogOwnership();
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LogSettingsStore_PersistsLargeSnapshotsAndFencesIdentityAndGeneration()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var server = new Uri("https://example.test/");
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id, MachineSecret = "test" }, token);
            var sources = Enumerable.Range(0, AgentLogSettingsSnapshot.MaximumSources).Select(index =>
                new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.TextFile,
                    $"source-{index}-" + new string('測', 200), "/" + new string('試', 1000), true, 1, AgentTextLogMode.SingleLine)).ToArray();
            var snapshot = AgentLogSettingsSnapshot.Create(sources);
            var store = new AgentLogSettingsStore(vault, server, id);
            var saved = await store.SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            Assert.IsGreaterThan(16_384L, new FileInfo(Path.Combine(vault.StateDirectory, "log-settings.dat")).Length);
            var restored = await new AgentLogSettingsStore(new IdentityVault(root), server, id).ReadAsync(token);
            Assert.IsNotNull(restored);
            Assert.AreEqual(snapshot.Revision, restored.Snapshot.Revision);
            Assert.HasCount(AgentLogSettingsSnapshot.MaximumSources, restored.Snapshot.Sources);
            await Assert.ThrowsExactlyAsync<AgentSettingsConflictException>(() => store.SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token));
            var inconsistent = AgentLogSettingsSnapshot.Create(sources.Select((source, index) => index == 0 ? source with { Enabled = false } : source));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.SaveAsync(inconsistent, saved.SaveToken, DateTimeOffset.UtcNow, token));
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = Guid.CreateVersion7(), MachineSecret = "new" }, token);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.ReadAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Collector_ResumesAfterRestartAndCommitsGapOnlyWhenCapacityAvailable()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var source = new LogSource(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.Journal, "system", 1);
            Assert.IsTrue(await queue.BindLogSourceAsync(owner, source.Id, null, source.Generation, false, token));
            var reader = new SequenceLogReader();
            var collector = new LogCollector(queue, TimeProvider.System);
            Assert.AreEqual(LogCollectStatus.Queued, await collector.CollectNextAsync(owner, source, reader, token));
            var first = await queue.PeekAsync(token);
            Assert.IsNotNull(first);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(first.Payload, out var saved));
            Assert.IsNull(saved!.Items[0].OccurredAt, "Do not fabricate a missing source timestamp.");
            Assert.AreEqual(LogCollectStatus.Full, await collector.CollectNextAsync(owner, source, reader, token));
            Assert.AreEqual(new LogCursor(source.Generation, "first"), await queue.ReadLogCursorAsync(owner, source.Id, token));
            Assert.IsTrue(await queue.AcknowledgeAsync(first.BatchId, token));
            var reopened = new DurableBatchQueue(new IdentityVault(root), id, 1024 * 1024, 1);
            var resumed = new LogCollector(reopened, TimeProvider.System);
            Assert.AreEqual(LogCollectStatus.Queued, await resumed.CollectNextAsync(owner, source, reader, token));
            Assert.AreEqual(new LogCursor(source.Generation, NativeLogCursor.Restart), await reopened.ReadLogCursorAsync(owner, source.Id, token));
            var pending = await reopened.PeekAsync(token);
            Assert.IsNotNull(pending);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(pending.Payload, out var parsed));
            Assert.AreEqual(AgentLogGapReason.CursorLost, parsed!.Items[0].Gap!.Reason);
            Assert.IsNull(parsed.Items[0].Gap!.AffectedCount);
            Assert.IsTrue(await reopened.AcknowledgeAsync(pending.BatchId, token));
            Assert.AreEqual(LogCollectStatus.Queued, await resumed.CollectNextAsync(owner, source, reader, token));
            var replayed = await reopened.PeekAsync(token);
            Assert.IsNotNull(replayed);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(replayed.Payload, out var replayedBatch));
            Assert.AreEqual("first available event", replayedBatch!.Items[0].Content);
            Assert.AreEqual(LogCollectStatus.End, await resumed.CollectNextAsync(owner, source, reader, token));
        }
        finally { Cleanup(root); }
    }

    private sealed class SequenceLogReader : ILogSourceReader
    {
        public ValueTask<LogReadResult> ReadAsync(string? cursor, CancellationToken token) => ValueTask.FromResult(cursor switch
        {
            null => new LogReadResult(LogReadStatus.Record, "first", new LogObservation(null, "source event")),
            "first" => new LogReadResult(LogReadStatus.Gap, NativeLogCursor.Restart, Gap: new(AgentLogGapReason.CursorLost, null)),
            NativeLogCursor.Restart => new LogReadResult(LogReadStatus.Record, "after", new LogObservation(null, "first available event")),
            _ => new LogReadResult(LogReadStatus.End)
        });
    }

    [TestMethod]
    public async Task LogQueue_CommitsCursorAndBatchTogetherAndFencesStaleGeneration()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var source = Guid.CreateVersion7();
            var generation = Guid.CreateVersion7();
            Assert.IsTrue(await queue.BindLogSourceAsync(owner, source, null, generation, false, token));
            var first = NewLogBatch(source);
            Assert.AreEqual(LogEnqueueResult.Queued, await queue.EnqueueLogAsync(owner, source, generation, null, "position-a", first, token));
            Assert.AreEqual(LogEnqueueResult.AlreadyCommitted, await queue.EnqueueLogAsync(owner, source, generation, null, "position-a", first, token));
            Assert.IsTrue(AgentLogBatchCodec.TryRead(first.Payload, out var decoded));
            var changed = first with { Payload = AgentLogBatchCodec.Serialize(decoded! with { Items = [decoded.Items[0] with { Content = "different" }] }) };
            Assert.AreEqual(LogEnqueueResult.CursorConflict, await queue.EnqueueLogAsync(owner, source, generation, null, "position-a", changed, token));
            var next = NewLogBatch(source);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(next.Payload, out var gapBatch));
            var gap = new AgentLogRecord(Guid.CreateVersion7(), null, "", null, null, null, null, null,
                new(AgentLogGapReason.Oversized, 1));
            next = next with { Payload = AgentLogBatchCodec.Serialize(gapBatch! with { Items = [gap] }) };
            Assert.AreEqual(LogEnqueueResult.Full, await queue.EnqueueLogAsync(owner, source, generation, "position-a", "position-b", next, token));
            Assert.AreEqual(new LogCursor(generation, "position-a"), await queue.ReadLogCursorAsync(owner, source, token));
            Assert.IsTrue(await queue.AcknowledgeAsync(first.BatchId, token));
            var reopened = new DurableBatchQueue(new IdentityVault(root), id, 1024 * 1024, 1);
            Assert.AreEqual(LogEnqueueResult.AlreadyCommitted, await reopened.EnqueueLogAsync(owner, source, generation, null, "position-a", first, token));
            Assert.AreEqual(LogEnqueueResult.Queued, await reopened.EnqueueLogAsync(owner, source, generation, "position-a", "position-b", next, token));
            Assert.AreEqual(new LogCursor(generation, "position-b"), await reopened.ReadLogCursorAsync(owner, source, token));
            var persisted = await reopened.PeekAsync(token);
            Assert.IsNotNull(persisted);
            Assert.IsTrue(AgentLogBatchCodec.TryRead(persisted.Payload, out var savedGap));
            Assert.AreEqual(gap, savedGap!.Items[0]);
            var replacement = Guid.CreateVersion7();
            Assert.IsTrue(await reopened.BindLogSourceAsync(owner, source, generation, replacement, false, token));
            Assert.AreEqual(new LogCursor(replacement, "position-b"), await reopened.ReadLogCursorAsync(owner, source, token));
            Assert.IsFalse(await reopened.BindLogSourceAsync(owner, source, generation, Guid.CreateVersion7(), false, token));
            Assert.AreEqual(LogEnqueueResult.CursorConflict, await reopened.EnqueueLogAsync(owner, source, generation, "position-b", "position-c", NewLogBatch(source), token));
            var resetGeneration = Guid.CreateVersion7();
            Assert.IsTrue(await reopened.BindLogSourceAsync(owner, source, replacement, resetGeneration, true, token));
            Assert.AreEqual(new LogCursor(resetGeneration, null), await reopened.ReadLogCursorAsync(owner, source, token));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => reopened.BindLogSourceAsync(owner, source, resetGeneration, resetGeneration, true, token));
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = Guid.CreateVersion7(), MachineSecret = "new" }, token);
            Assert.AreEqual(LogEnqueueResult.OwnerChanged, await reopened.EnqueueLogAsync(owner, source, resetGeneration, null, "position-c", NewLogBatch(source), token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LogQueue_ConcurrentProducersCannotAdvanceTheSameCursorTwice()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var source = Guid.CreateVersion7();
            var generation = Guid.CreateVersion7();
            var first = new DurableBatchQueue(vault, id, 1024 * 1024, 10);
            var second = new DurableBatchQueue(new IdentityVault(root), id, 1024 * 1024, 10);
            Assert.IsTrue(await first.BindLogSourceAsync(owner, source, null, generation, false, token));
            var left = NewLogBatch(source);
            var right = NewLogBatch(source);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var leftTask = Task.Run(async () => { await start.Task.WaitAsync(token); return await first.EnqueueLogAsync(owner, source, generation, null, "left", left, token); }, token);
            var rightTask = Task.Run(async () => { await start.Task.WaitAsync(token); return await second.EnqueueLogAsync(owner, source, generation, null, "right", right, token); }, token);
            start.SetResult();
            var outcomes = await Task.WhenAll(leftTask, rightTask);
            Assert.AreEqual(1, outcomes.Count(x => x == LogEnqueueResult.Queued));
            Assert.AreEqual(1, outcomes.Count(x => x == LogEnqueueResult.CursorConflict));
            Assert.AreEqual(1L, (await first.GetStatusAsync(token)).TotalBatches);
            var cursor = await first.ReadLogCursorAsync(owner, source, token);
            Assert.AreEqual(outcomes[0] == LogEnqueueResult.Queued ? "left" : "right", cursor!.Position);
            Assert.AreEqual(outcomes[0] == LogEnqueueResult.Queued ? left.BatchId : right.BatchId, (await first.PeekAsync(token))!.BatchId);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LogQueue_StorageFailureRollsBackBatchUsageAndCursor()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 10);
            var source = Guid.CreateVersion7();
            var generation = Guid.CreateVersion7();
            Assert.IsTrue(await queue.BindLogSourceAsync(owner, source, null, generation, false, token));
            await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                await db.OpenAsync(token);
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_log_cursor BEFORE UPDATE OF cursor ON log_sources BEGIN SELECT RAISE(ABORT, 'test cursor failure'); END";
                await command.ExecuteNonQueryAsync(token);
            }
            var batch = NewLogBatch(source);
            var error = await Assert.ThrowsExactlyAsync<SqliteException>(() => queue.EnqueueLogAsync(owner, source, generation, null, "position-a", batch, token));
            Assert.Contains("test cursor failure", error.Message);
            Assert.IsNull(await queue.PeekAsync(token));
            var status = await queue.GetStatusAsync(token);
            Assert.AreEqual(0L, status.TotalBatches);
            Assert.AreEqual(0L, status.TotalBytes);
            Assert.AreEqual(new LogCursor(generation, null), await queue.ReadLogCursorAsync(owner, source, token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LogQueue_CorruptCursorFailsWithoutAdvancingOrOverwritingIt()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 10);
            var source = Guid.CreateVersion7();
            var generation = Guid.CreateVersion7();
            Assert.IsTrue(await queue.BindLogSourceAsync(owner, source, null, generation, false, token));
            await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                await db.OpenAsync(token);
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE log_sources SET cursor = char(0) WHERE source_id = $source";
                command.Parameters.AddWithValue("$source", source.ToString("D"));
                await command.ExecuteNonQueryAsync(token);
            }
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => queue.ReadLogCursorAsync(owner, source, token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => queue.BindLogSourceAsync(owner, source, generation, Guid.CreateVersion7(), true, token));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => queue.EnqueueLogAsync(owner, source, generation, null, "next", NewLogBatch(source), token));
            Assert.AreEqual(0L, (await queue.GetStatusAsync(token)).TotalBatches);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LogQueue_UpgradesExistingQueueOnlyForItsOwner()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 10);
            var oldId = Guid.CreateVersion7();
            var payload = new byte[] { 1, 2, 3 };
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(oldId, DateTimeOffset.UtcNow, 1, payload, token));
            var source = Guid.CreateVersion7();
            var generation = Guid.CreateVersion7();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => queue.BindLogSourceAsync(owner with { AgentId = Guid.CreateVersion7() }, source, null, generation, false, token));
            await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                await db.OpenAsync(token);
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
                Assert.AreEqual(2L, (long)(await command.ExecuteScalarAsync(token))!);
            }
            Assert.IsTrue(await queue.BindLogSourceAsync(owner, source, null, generation, false, token));
            await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                await db.OpenAsync(token);
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
                Assert.AreEqual(3L, (long)(await command.ExecuteScalarAsync(token))!);
            }
            var retained = await queue.PeekAsync(token);
            Assert.IsNotNull(retained);
            Assert.AreEqual(oldId, retained.BatchId);
            CollectionAssert.AreEqual(payload, retained.Payload);
            Assert.AreEqual(new LogCursor(generation, null), await queue.ReadLogCursorAsync(owner, source, token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task LogQueue_FailedUpgradeLeavesOriginalQueueIntact()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 10);
            var oldId = Guid.CreateVersion7();
            var payload = new byte[] { 4, 5, 6 };
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(oldId, DateTimeOffset.UtcNow, 1, payload, token));
            await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                await db.OpenAsync(token);
                using var command = db.CreateCommand(); command.CommandText = "CREATE TABLE log_sources (unexpected INTEGER)";
                await command.ExecuteNonQueryAsync(token);
            }
            await Assert.ThrowsExactlyAsync<SqliteException>(() => queue.BindLogSourceAsync(owner, Guid.CreateVersion7(), null, Guid.CreateVersion7(), false, token));
            await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                await db.OpenAsync(token);
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
                Assert.AreEqual(2L, (long)(await command.ExecuteScalarAsync(token))!);
            }
            var retained = await queue.PeekAsync(token);
            Assert.IsNotNull(retained);
            Assert.AreEqual(oldId, retained.BatchId);
            CollectionAssert.AreEqual(payload, retained.Payload);
        }
        finally { Cleanup(root); }
    }

    private static QueuedBatch NewLogBatch(Guid source)
    {
        var now = DateTimeOffset.UtcNow;
        var batch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now, source,
            AgentLogSourceKind.Journal, "system", 1, [new(Guid.CreateVersion7(), now, "record", null, null, null, null, null)]);
        return new(batch.BatchId, batch.EnqueuedAt, 1, AgentLogBatchCodec.Serialize(batch));
    }

    [TestMethod]
    public async Task MetricSettingsStore_PreservesLastGoodAndFencesStaleOrOtherIdentity()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var server = new Uri("https://example.test/");
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id, MachineSecret = "test" }, token);
            var store = new AgentMetricSettingsStore(vault, server, id);
            Assert.IsNull(await store.ReadAsync(token));
            var first = await store.SaveAsync(AgentMetricSettingsSnapshot.Create(AgentMetricSettings.Default), null, DateTimeOffset.UtcNow, token);
            var second = await store.SaveAsync(AgentMetricSettingsSnapshot.Create(new(45, 90)), first.SaveToken, DateTimeOffset.UtcNow, token);
            await Assert.ThrowsAsync<AgentSettingsConflictException>(() => store.SaveAsync(first.Snapshot, first.SaveToken, DateTimeOffset.UtcNow, token));
            Assert.AreEqual(second, await store.ReadAsync(token));
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = Guid.CreateVersion7(), MachineSecret = "test" }, token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task MetricPublisher_AmbiguousHeadKeepsLaterUnattemptedBatchesDistinct()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var now = DateTimeOffset.UtcNow;
            var points = Enumerable.Range(0, 501).Select(i => new AgentMetricPoint(Guid.CreateVersion7(), now,
                AgentMetricKind.MemoryTotalBytes, "test", null, i)).ToArray();
            var publisher = new MetricSamplePublisher(new DurableBatchQueue(vault, id), owner);
            publisher.Prepare(points, now);
            await using (vault.Acquire())
                await Assert.ThrowsExactlyAsync<IOException>(() => publisher.FlushAsync(token));
            Assert.IsTrue(publisher.PendingOutcomeUnknown);
            Assert.AreEqual(3, publisher.PendingBatches);
            Assert.AreEqual(2, publisher.KnownUnqueuedBatches);
            Assert.AreEqual(251L, publisher.KnownUnqueuedRecords);
            Assert.AreEqual(EnqueueResult.Queued, await publisher.FlushAsync(token));
            Assert.IsFalse(publisher.PendingOutcomeUnknown);
            Assert.AreEqual(0, publisher.KnownUnqueuedBatches);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task MetricPublisher_SampleLargerThanQueueProgressesInOrder()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var now = DateTimeOffset.UtcNow;
            var points = Enumerable.Range(0, 501).Select(i => new AgentMetricPoint(Guid.CreateVersion7(), now,
                AgentMetricKind.MemoryTotalBytes, "test", null, i)).ToArray();
            var publisher = new MetricSamplePublisher(queue, owner);
            publisher.Prepare(points, now);
            var actual = new List<Guid>();
            for (var i = 0; i < 3; i++)
            {
                var status = await publisher.FlushAsync(token);
                Assert.AreEqual(i < 2 ? EnqueueResult.Full : EnqueueResult.Queued, status);
                var batch = await queue.PeekAsync(token);
                Assert.IsNotNull(batch);
                Assert.IsTrue(AgentMetricBatchCodec.TryRead(batch.Payload, out var decoded));
                Assert.IsNotNull(decoded);
                actual.AddRange(decoded.Items.Select(x => x.MessageId));
                Assert.IsTrue(await queue.AcknowledgeAsync(batch.BatchId, token));
            }
            Assert.IsFalse(publisher.HasPending);
            CollectionAssert.AreEqual(points.Select(x => x.MessageId).ToArray(), actual);
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task MetricPublisher_RetainsSampleWhileQueueIsFullAndRetriesSamePoints()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 100_000, 1);
            var existing = Guid.CreateVersion7();
            await queue.EnqueueAsync(existing, DateTimeOffset.UtcNow, 1, new byte[] { 1 }, token);
            var now = DateTimeOffset.UtcNow;
            var point = new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.MemoryTotalBytes, "test", null, 1024);
            var publisher = new MetricSamplePublisher(queue, owner);
            publisher.Prepare([point], now);
            Assert.AreEqual(EnqueueResult.Full, await publisher.FlushAsync(token));
            Assert.IsTrue(publisher.HasPending);
            Assert.Throws<InvalidOperationException>(() => publisher.Prepare([point], now));
            Assert.IsTrue(await queue.AcknowledgeAsync(existing, token));
            Assert.AreEqual(EnqueueResult.Queued, await publisher.FlushAsync(token));
            Assert.IsFalse(publisher.HasPending);
            var saved = await queue.PeekAsync(token);
            Assert.IsNotNull(saved);
            Assert.IsTrue(AgentMetricBatchCodec.TryRead(saved.Payload, out var batch));
            Assert.IsNotNull(batch);
            Assert.AreEqual(point, batch.Items.Single());
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_OwnerIsRecheckedUnderIdentityLeaseBeforeWriting()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var agentId = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(agentId, "https://example.test/", vault.StateDirectory);
            var queue = new DurableBatchQueue(vault, agentId, 100, 4);
            var batch = new QueuedBatch(Guid.CreateVersion7(), DateTimeOffset.UtcNow, 1, [1]);
            Assert.AreEqual(EnqueueResult.OwnerChanged, await queue.EnqueueManyForOwnerAsync([batch], owner, token));
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = agentId, MachineSecret = "test" }, token);
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueManyForOwnerAsync([batch], owner, token));
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = "https://other.test/", AgentId = agentId, MachineSecret = "test" }, token);
            var next = new QueuedBatch(Guid.CreateVersion7(), DateTimeOffset.UtcNow, 1, [2]);
            Assert.AreEqual(EnqueueResult.OwnerChanged, await queue.EnqueueManyForOwnerAsync([next], owner, token));
            Assert.AreEqual(new QueueStatus(1, 1, 0), await queue.GetStatusAsync(token));
        }
        finally { Cleanup(root); }
    }

    [TestMethod]
    public async Task Queue_MultipleBatchesAreAtomicAndRetryable()
    {
        var root = NewRoot();
        try
        {
            var token = TestContext.CancellationToken;
            var owner = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            var first = new QueuedBatch(Guid.CreateVersion7(), now, 1, [1, 2]);
            var second = new QueuedBatch(Guid.CreateVersion7(), now, 1, [3, 4]);
            var small = new DurableBatchQueue(new IdentityVault(root), owner, 3, 2);
            Assert.AreEqual(EnqueueResult.ExceedsCapacity, await small.EnqueueManyAsync([first, second], token));
            Assert.AreEqual(new QueueStatus(0, 0, 0), await small.GetStatusAsync(token));
            var queue = new DurableBatchQueue(new IdentityVault(root), owner, 10, 2);
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER test_reject_second BEFORE INSERT ON batches WHEN NEW.payload = X'0304' BEGIN SELECT RAISE(ABORT, 'injected second insert failure'); END";
                command.ExecuteNonQuery();
            }
            await Assert.ThrowsAsync<SqliteException>(() => queue.EnqueueManyAsync([first, second], token));
            Assert.AreEqual(new QueueStatus(0, 0, 0), await queue.GetStatusAsync(token));
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString()))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "DROP TRIGGER test_reject_second";
                command.ExecuteNonQuery();
            }
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueManyAsync([first, second], token));
            Assert.AreEqual(EnqueueResult.AlreadyQueued, await queue.EnqueueManyAsync([first, second], token));
            Assert.AreEqual(EnqueueResult.ConflictingBatch, await queue.EnqueueManyAsync([first with { Payload = [9] }, second], token));
            Assert.AreEqual(new QueueStatus(4, 2, 0), await queue.GetStatusAsync(token));
            Assert.AreEqual(first.BatchId, (await queue.PeekAsync(token))!.BatchId);
            Assert.IsTrue(await queue.AcknowledgeAsync(first.BatchId, token));
            Assert.AreEqual(second.BatchId, (await queue.PeekAsync(token))!.BatchId);
        }
        finally { Cleanup(root); }
    }

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
            await Assert.ThrowsAsync<SqliteException>(() => queue.AcknowledgeAsync(original, token));
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
                using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version = 4"; command.ExecuteNonQuery();
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

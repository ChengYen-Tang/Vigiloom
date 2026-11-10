using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class MetricSamplingCycleTests(TestContext context)
{
    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task ShutdownFlush_WaitsForSenderToFreeQueueSpace()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomMetricDrainTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var existing = Guid.CreateVersion7();
            await queue.EnqueueAsync(existing, DateTimeOffset.UtcNow, 1, new byte[] { 1 }, token);
            using var cycle = new MetricSamplingCycle(owner, queue, TimeProvider.System);
            Assert.AreEqual(EnqueueResult.Full, (await cycle.RunAsync(AgentMetricSettings.Default, token)).Status);
            Assert.IsGreaterThan(0, cycle.PendingBatches);
            Assert.IsGreaterThan(0L, cycle.PendingRecords);
            Assert.IsFalse(cycle.PendingOutcomeUnknown, "An explicit full response is backpressure, not an ambiguous enqueue.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var flushing = cycle.FlushForShutdownAsync(deadline.Token);
            try
            {
                Assert.IsFalse(flushing.IsCompleted, "A full queue must not be mistaken for a successful flush.");
                while (!flushing.IsCompleted)
                {
                    if (await queue.PeekAsync(deadline.Token) is { } batch)
                        Assert.IsTrue(await queue.AcknowledgeAsync(batch.BatchId, deadline.Token));
                    await Task.Delay(25, deadline.Token);
                }
                Assert.AreEqual(EnqueueResult.Queued, await flushing);
                Assert.IsFalse(cycle.HasPending);
                Assert.AreEqual(0L, cycle.PendingRecords);
            }
            finally
            {
                deadline.Cancel();
                try { await flushing; } catch (Exception) { /* Preserve the test's original failure. */ }
            }
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task QueueFailure_DoesNotStarveDeferredProcessSample()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomMetricQueueFailureTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var queue = new DurableBatchQueue(vault, id, 16 * 1024 * 1024, 100);
            using var cycle = new MetricSamplingCycle(owner, queue, clock);
            await using (var held = vault.Acquire())
                await Assert.ThrowsExactlyAsync<IOException>(() => cycle.RunAsync(AgentMetricSettings.Default, token));
            Assert.IsTrue(cycle.HasPending);
            Assert.IsTrue(cycle.PendingOutcomeUnknown, "An interrupted enqueue must not be classified as a confirmed loss.");
            clock.Advance(TimeSpan.FromSeconds(30));
            var result = await cycle.RunAsync(AgentMetricSettings.Default, token);
            Assert.AreEqual(EnqueueResult.Queued, result.Status);
            Assert.IsFalse(cycle.PendingOutcomeUnknown);
            var foundProcess = false;
            var foundSecondHost = false;
            while (await queue.PeekAsync(token) is { } batch)
            {
                Assert.IsTrue(AgentMetricBatchCodec.TryRead(batch.Payload, out var decoded));
                Assert.IsNotNull(decoded);
                foreach (var item in decoded.Items)
                {
                    if (item.OccurredAt == clock.GetUtcNow() && item.Kind == AgentMetricKind.MemoryTotalBytes)
                    {
                        Assert.IsTrue(foundProcess, "The deferred process sample must precede the next host sample.");
                        foundSecondHost = true;
                    }
                    if (item.Kind == AgentMetricKind.ProcessWorkingSetBytes) foundProcess = true;
                }
                Assert.IsTrue(await queue.AcknowledgeAsync(batch.BatchId, token));
            }
            Assert.IsTrue(foundProcess);
            Assert.IsTrue(foundSecondHost);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FlushPending_QueuesExistingSampleWithoutCollectingAgain()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomMetricShutdownTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var queue = new DurableBatchQueue(vault, id, 1024 * 1024, 1);
            var existing = Guid.CreateVersion7();
            await queue.EnqueueAsync(existing, DateTimeOffset.UtcNow, 1, new byte[] { 1 }, token);
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            using var cycle = new MetricSamplingCycle(owner, queue, clock);
            var first = await cycle.RunAsync(AgentMetricSettings.Default, token);
            Assert.AreEqual(EnqueueResult.Full, first.Status);
            Assert.IsTrue(cycle.HasPending);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => cycle.FlushForShutdownAsync(cancelled.Token));
                Assert.IsTrue(cycle.HasPending);
            }
            Assert.IsTrue(await queue.AcknowledgeAsync(existing, token));
            var delivered = new HashSet<Guid>();
            for (var pass = 0; pass < 100 && cycle.HasPending; pass++)
            {
                var task = cycle.FlushPendingAsync(token);
                Assert.IsNotNull(task);
                var status = await task;
                Assert.IsTrue(status is EnqueueResult.Queued or EnqueueResult.Full);
                var batch = await queue.PeekAsync(token);
                Assert.IsNotNull(batch);
                Assert.IsTrue(AgentMetricBatchCodec.TryRead(batch.Payload, out var decoded));
                Assert.IsNotNull(decoded);
                foreach (var item in decoded.Items) Assert.IsTrue(delivered.Add(item.MessageId));
                Assert.IsTrue(await queue.AcknowledgeAsync(batch.BatchId, token));
            }
            Assert.IsFalse(cycle.HasPending);
            Assert.IsNotEmpty(delivered);
            Assert.IsNull(cycle.FlushPendingAsync(token));
            // Host sampling was blocked before process sampling began. Once its
            // pending batches drain, process collection gets priority even if
            // the host interval also elapsed while waiting for queue capacity.
            clock.Advance(TimeSpan.FromSeconds(30));
            var next = await cycle.RunAsync(AgentMetricSettings.Default, token);
            var processBatch = await queue.PeekAsync(token);
            Assert.IsNotNull(processBatch);
            Assert.IsTrue(AgentMetricBatchCodec.TryRead(processBatch.Payload, out var processSample));
            Assert.IsNotNull(processSample);
            Assert.IsTrue(processSample.Items.All(x => x.Kind is AgentMetricKind.ProcessCpuPercent or
                AgentMetricKind.ProcessWorkingSetBytes or AgentMetricKind.CollectionUnavailable));
            Assert.IsTrue(next.Status is EnqueueResult.Queued or EnqueueResult.Full);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task Worker_StartsSamplesAndStops()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomMetricWorkerTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var server = "https://example.test/";
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = server, AgentId = id, MachineSecret = secret }, token);
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = server
            });
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<AgentMetricSettingsRuntime>();
            builder.Services.AddHostedService<MetricWorker>();
            using var host = builder.Build();
            try
            {
                await host.StartAsync(token);
                var queue = new DurableBatchQueue(vault, id);
                QueuedBatch? first = null;
                IOException? lastSharingViolation = null;
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (first is null && DateTimeOffset.UtcNow < deadline)
                {
                    try { first = await queue.PeekAsync(token); }
                    catch (IOException error) when (OperatingSystem.IsWindows() && (error.HResult & 0xffff) == 32)
                    {
                        // The running worker may temporarily hold the exclusive identity lease.
                        lastSharingViolation = error;
                    }
                    if (first is null) await Task.Delay(100, token);
                }
                Assert.IsNotNull(first, $"No batch arrived before the deadline. Last sharing violation: {lastSharingViolation}");
                Assert.IsTrue(AgentMetricBatchCodec.TryRead(first.Payload, out var batch));
                Assert.IsNotNull(batch);
                Assert.IsTrue(batch.Items.Any(x => x.Kind == AgentMetricKind.MemoryTotalBytes));
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Cycle_CollectsQueuesAndRespectsIndependentDueTimes()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomMetricCycleTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var id = Guid.CreateVersion7();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = id, MachineSecret = "test" }, token);
            var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
            var queue = new DurableBatchQueue(vault, id, 16 * 1024 * 1024, 100);
            using var cycle = new MetricSamplingCycle(owner, queue, clock);
            var settings = new AgentMetricSettings(30, 10);
            var first = await cycle.RunAsync(settings, token);
            Assert.AreEqual(EnqueueResult.Queued, first.Status);
            var kinds = new List<AgentMetricKind>();
            while (await queue.PeekAsync(token) is { } batch)
            {
                Assert.IsTrue(AgentMetricBatchCodec.TryRead(batch.Payload, out var decoded));
                Assert.IsNotNull(decoded);
                kinds.AddRange(decoded.Items.Select(x => x.Kind));
                Assert.IsTrue(await queue.AcknowledgeAsync(batch.BatchId, token));
            }
            Assert.Contains(AgentMetricKind.MemoryTotalBytes, kinds);
            Assert.Contains(AgentMetricKind.CollectionUnavailable, kinds);
            var early = await cycle.RunAsync(settings, token);
            Assert.IsNull(early.Status);
            Assert.AreEqual(TimeSpan.FromSeconds(10), early.Delay);
            clock.Advance(TimeSpan.FromSeconds(10));
            var next = await cycle.RunAsync(settings, token);
            if (next.Status == EnqueueResult.Queued)
            {
                while (await queue.PeekAsync(token) is { } batch)
                {
                    Assert.IsTrue(AgentMetricBatchCodec.TryRead(batch.Payload, out var decoded));
                    Assert.IsNotNull(decoded);
                    Assert.IsTrue(decoded.Items.All(x => x.Kind is AgentMetricKind.ProcessCpuPercent or AgentMetricKind.ProcessWorkingSetBytes or AgentMetricKind.CollectionUnavailable));
                    Assert.IsTrue(await queue.AcknowledgeAsync(batch.BatchId, token));
                }
            }
            else Assert.Contains("process", next.UnavailableSources);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = "https://other.test/", AgentId = id, MachineSecret = "test" }, token);
            clock.Advance(TimeSpan.FromSeconds(30));
            var changed = await cycle.RunAsync(settings, token);
            Assert.AreEqual(EnqueueResult.OwnerChanged, changed.Status);
            Assert.IsNull(await queue.PeekAsync(token));
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}

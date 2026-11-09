using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MetricWorkerPolicyTests(TestContext context)
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task Worker_DelaysAndResumesWhenCpuOrIoPolicyChanges(bool cpu)
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomMetricBudgetTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        Exception? failure = null;
        var workerStopped = true;
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = owner.AgentId, MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var runtime = new AgentMetricSettingsRuntime();
            runtime.Bind(owner);
            void Publish(bool restrictive) => runtime.Publish(owner, new AgentSavedMetricSettings(
                AgentMetricSettingsSnapshot.Create(new(10, 10, cpu && restrictive ? 100 : 60000, 4096, 2000, 16, !cpu && restrictive ? 1 : 4096)),
                DateTimeOffset.UtcNow, Guid.CreateVersion7(), owner.AgentId, owner.Server));
            Publish(true);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = vault.StateDirectory,
                ["Vigiloom:Agent:Server"] = owner.Server
            }).Build();
            var logger = new CapturingLogger();
            var monitor = new ControlledMonitor(cpu);
            using var worker = new MetricWorker(runtime, config, TimeProvider.System, logger, monitor);
            Exception? workerFailure = null;
            try
            {
                workerStopped = false;
                await worker.StartAsync(token);
                var kind = cpu ? "CPU" : "I/O";
                await WaitForAsync(() => logger.Contains($"delayed by the {kind} budget"), token);
                Assert.IsGreaterThanOrEqualTo(2, monitor.IoReads);
                var before = monitor.IoReads;
                var queue = new DurableBatchQueue(vault, owner.AgentId);
                var batchesBefore = (await queue.GetStatusAsync(token)).TotalBatches;
                var checksBefore = monitor.MemoryReads;
                await WaitForAsync(() => monitor.MemoryReads > checksBefore, token);
                Assert.AreEqual(before, monitor.IoReads, "No new cycle should start while the budget is exhausted.");
                Publish(false);
                await WaitForAsync(() => logger.Contains($"resumed after the {kind} budget delay"), token);
                await WaitForAsync(() => monitor.IoReads >= before + 2, token);
                using var queueDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                queueDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                while (true)
                {
                    try { if ((await queue.GetStatusAsync(queueDeadline.Token)).TotalBatches > batchesBefore) break; }
                    catch (IOException) { /* The running worker may hold the identity lease briefly. */ }
                    await Task.Delay(100, queueDeadline.Token);
                }
            }
            catch (Exception error) { workerFailure = error; throw; }
            finally
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await worker.StopAsync(deadline.Token);
                    if (worker.ExecuteTask is { } execution) await execution.WaitAsync(deadline.Token);
                    workerStopped = true;
                }
                catch (Exception cleanupError) when (workerFailure is not null) { throw new AggregateException(workerFailure, cleanupError); }
            }
            var status = await new DurableBatchQueue(vault, owner.AgentId).GetStatusAsync(token);
            Assert.IsGreaterThan(0L, status.TotalBatches);
            Assert.IsTrue(monitor.Disposed);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try
            {
                if (!workerStopped) throw new InvalidOperationException($"Worker did not stop; test directory was retained: {root}");
                var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupError) when (failure is not null) { throw new AggregateException(failure, cleanupError); }
        }
    }

    [TestMethod]
    [Timeout(40000, CooperativeCancellation = true)]
    public async Task Worker_DelaysSamplingAboveMemoryThresholdAndResumesAfterPolicyChange()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomMetricPolicyTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        Exception? primaryFailure = null;
        var workerStopped = true;
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = owner.AgentId, MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var runtime = new AgentMetricSettingsRuntime();
            runtime.Bind(owner);
            void Publish(int memory) => runtime.Publish(owner, new AgentSavedMetricSettings(
                AgentMetricSettingsSnapshot.Create(new(10, 10, 60000, memory, 2000, 16, 4096)), DateTimeOffset.UtcNow,
                Guid.CreateVersion7(), owner.AgentId, owner.Server));
            Publish(64);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = vault.StateDirectory,
                ["Vigiloom:Agent:Server"] = owner.Server
            }).Build();
            var logger = new CapturingLogger();
            using var worker = new MetricWorker(runtime, config, TimeProvider.System, logger);
            var pressure = new byte[128 * 1024 * 1024];
            for (var i = 0; i < pressure.Length; i += 4096) pressure[i] = 1;
            Exception? workerFailure = null;
            try
            {
                workerStopped = false;
                await worker.StartAsync(token);
                await WaitForAsync(() => logger.Contains("delayed by the memory budget"), token);
                Assert.IsFalse(logger.Contains("Agent metric queue: Queued"));
                var before = await new DurableBatchQueue(vault, owner.AgentId).GetStatusAsync(token);
                Assert.AreEqual(0L, before.TotalBatches);
                Publish(4096);
                await WaitForAsync(() => logger.Contains("resumed after the memory budget delay"), token);
                await WaitForAsync(() => logger.Contains("Agent metric queue: Queued"), token);
                GC.KeepAlive(pressure);
            }
            catch (Exception error) { workerFailure = error; throw; }
            finally
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await worker.StopAsync(deadline.Token);
                    if (worker.ExecuteTask is { } execution) await execution.WaitAsync(deadline.Token);
                    workerStopped = true;
                }
                catch (Exception cleanupError) when (workerFailure is not null) { throw new AggregateException(workerFailure, cleanupError); }
            }
            var status = await new DurableBatchQueue(vault, owner.AgentId).GetStatusAsync(token);
            Assert.IsGreaterThan(0L, status.TotalBatches);
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            try
            {
                if (!workerStopped) throw new InvalidOperationException($"Worker did not stop; test directory was retained: {root}");
                var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupError) when (primaryFailure is not null) { throw new AggregateException(primaryFailure, cleanupError); }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }

    private sealed class CapturingLogger : ILogger<MetricWorker>
    {
        private readonly ConcurrentQueue<string> messages = new();
        public bool Contains(string value) => messages.Any(message => message.Contains(value, StringComparison.Ordinal));
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }

    private sealed class ControlledMonitor(bool cpu) : IMetricResourceMonitor
    {
        private int cpuReads;
        private int ioReads;
        private int memoryReads;
        public int IoReads => Volatile.Read(ref ioReads);
        public int MemoryReads => Volatile.Read(ref memoryReads);
        public bool Disposed { get; private set; }
        public long ReadWorkingSetBytes() { Interlocked.Increment(ref memoryReads); return 1024; }
        public TimeSpan ReadCpuTime() => TimeSpan.FromMilliseconds(Interlocked.Increment(ref cpuReads) * (cpu ? 200 : 0));
        public ValueTask<ProcessIoSnapshot> ReadIoAsync(CancellationToken token) =>
            ValueTask.FromResult(new ProcessIoSnapshot((ulong)Interlocked.Increment(ref ioReads) * (cpu ? 0UL : 2UL * 1024 * 1024), 0));
        public void Dispose() => Disposed = true;
    }
}

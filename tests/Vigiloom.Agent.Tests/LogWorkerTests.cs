using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting.Logs;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
[DoNotParallelize]
public sealed class LogWorkerTests(TestContext context)
{
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Worker_UsesNewResourcePolicyAfterWaitingForSharedBudget()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomLogPolicyTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        var stopped = true;
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = owner.AgentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var path = Path.Combine(root, "app.log");
            await File.WriteAllTextAsync(path, "event\n", token);
            var source = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.TextFile,
                "app", path, true, 1, AgentTextLogMode.SingleLine);
            var snapshot = AgentLogSettingsSnapshot.Create([source]);
            var saved = await new AgentLogSettingsStore(vault, new Uri(owner.Server), owner.AgentId).SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            var logs = new AgentLogSettingsRuntime(); logs.Bind(owner); logs.Publish(owner, saved);
            var metrics = new AgentMetricSettingsRuntime(); metrics.Bind(owner);
            void Publish(int memory) => metrics.Publish(owner, new AgentSavedMetricSettings(
                AgentMetricSettingsSnapshot.Create(new(30, 60, 3000, memory)), DateTimeOffset.UtcNow,
                Guid.CreateVersion7(), owner.AgentId, owner.Server));
            Publish(512);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Vigiloom:Agent:StateDirectory"] = root, ["Vigiloom:Agent:Server"] = owner.Server }).Build();
            var budget = new SamplingBudgetCoordinator(TimeProvider.System);
            var held = await budget.AcquireAsync(token);
            var logger = new BudgetLogger();
            using var worker = new LogWorker(logs, metrics, configuration, TimeProvider.System, logger, new LowUsageMonitor(100L * 1024 * 1024), budget);
            try
            {
                stopped = false;
                await worker.StartAsync(token);
                // The worker owns the producer lease before it waits for the shared budget.
                var acquired = false;
                for (var attempt = 0; attempt < 100 && !acquired; attempt++)
                {
                    IAsyncDisposable? contender = null;
                    try { contender = new IdentityVault(root).AcquireLogOwnership(); }
                    catch (IOException) { acquired = true; }
                    finally { if (contender is not null) await contender.DisposeAsync(); }
                    if (!acquired) await Task.Delay(50, token);
                }
                Assert.IsTrue(acquired);
                Publish(64);
                await held.DisposeAsync();
                await logger.Throttled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                var queue = new DurableBatchQueue(vault, owner.AgentId);
                Assert.IsNull(await queue.PeekAsync(token));
                Publish(512);
                QueuedBatch? batch = null;
                for (var attempt = 0; attempt < 100 && batch is null; attempt++)
                { batch = await queue.PeekAsync(token); if (batch is null) await Task.Delay(100, token); }
                Assert.IsNotNull(batch);
            }
            finally
            {
                await held.DisposeAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await worker.StopAsync(deadline.Token);
                if (worker.ExecuteTask is { } execution) await execution.WaitAsync(deadline.Token);
                stopped = true;
            }
        }
        finally
        {
            if (!stopped) throw new InvalidOperationException($"Worker did not stop; test directory retained: {root}");
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Worker_CollectsConfiguredTextAndReleasesLeaseOnShutdown()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomLogWorkerTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        var stopped = true;
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = owner.AgentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var path = Path.Combine(root, "app.log");
            await File.WriteAllTextAsync(path, "worker-event\n", token);
            var source = new AgentLogSourceSetting(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceKind.TextFile,
                "app", path, true, 1, AgentTextLogMode.SingleLine);
            var snapshot = AgentLogSettingsSnapshot.Create([source]);
            var saved = await new AgentLogSettingsStore(vault, new Uri(owner.Server), owner.AgentId).SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            var logs = new AgentLogSettingsRuntime(); logs.Bind(owner); logs.Publish(owner, saved);
            var metrics = new AgentMetricSettingsRuntime(); metrics.Bind(owner);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Vigiloom:Agent:StateDirectory"] = root, ["Vigiloom:Agent:Server"] = owner.Server }).Build();
            using var worker = new LogWorker(logs, metrics, configuration, TimeProvider.System, NullLogger<LogWorker>.Instance, new LowUsageMonitor());
            try
            {
                stopped = false;
                await worker.StartAsync(token);
                var queue = new DurableBatchQueue(vault, owner.AgentId);
                QueuedBatch? batch = null;
                for (var attempt = 0; attempt < 100 && batch is null; attempt++)
                { batch = await queue.PeekAsync(token); if (batch is null) await Task.Delay(100, token); }
                Assert.IsNotNull(batch);
                Assert.IsTrue(AgentLogBatchCodec.TryRead(batch.Payload, out var parsed));
                Assert.AreEqual("worker-event\n", parsed!.Items[0].Content);
                IAsyncDisposable? contender = null;
                try { Assert.ThrowsExactly<IOException>(() => contender = new IdentityVault(root).AcquireLogOwnership()); }
                finally { if (contender is not null) await contender.DisposeAsync(); }
            }
            finally
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await worker.StopAsync(deadline.Token);
                if (worker.ExecuteTask is { } execution) await execution.WaitAsync(deadline.Token);
                stopped = true;
            }
            await using var nextOwner = new IdentityVault(root).AcquireLogOwnership();
        }
        finally
        {
            if (!stopped) throw new InvalidOperationException($"Worker did not stop; test directory retained: {root}");
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class LowUsageMonitor(long workingSetBytes = 1) : IMetricResourceMonitor
    {
        public long ReadWorkingSetBytes() => workingSetBytes;
        public TimeSpan ReadCpuTime() => TimeSpan.Zero;
        public ValueTask<ProcessIoSnapshot> ReadIoAsync(CancellationToken token) => ValueTask.FromResult(new ProcessIoSnapshot(0, 0));
        public void Dispose() { }
    }

    private sealed class BudgetLogger : ILogger<LogWorker>
    {
        public TaskCompletionSource Throttled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("delayed by the shared resource budget", StringComparison.Ordinal)) Throttled.TrySetResult();
        }
    }
}

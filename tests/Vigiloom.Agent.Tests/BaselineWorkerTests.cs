using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Core.Baseline;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting.Baseline;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class BaselineWorkerTests(TestContext context)
{
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Worker_CancelsSlowCollectionAndDoesNotQueuePartialResult()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomBaselineWorkerTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        var stopped = true;
        try
        {
            var token = context.CancellationToken;
            var clock = new FakeTimeProvider();
            var vault = new IdentityVault(root);
            var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", vault.StateDirectory);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = owner.Server, AgentId = owner.AgentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var selection = new BaselineSelection(Guid.CreateVersion7(), Guid.CreateVersion7(), "AAAAAAAAAAA=",
                new(BaselineSources.LinuxShadowCheck, "Shadow", "linux", ["ubuntu:24.04"], BaselineSources.LinuxShadowOperation,
                    BaselineSources.LinuxShadowTarget, "0640-or-stricter", "https://example.test/rule", "BSD-3-Clause"));
            var runtime = new AgentBaselineSettingsRuntime();
            runtime.Bind(owner);
            runtime.Publish(owner, new(BaselineSettingsSnapshot.Create(21600, [selection]), clock.GetUtcNow(), Guid.CreateVersion7(), owner.AgentId, owner.Server));
            var metrics = new AgentMetricSettingsRuntime();
            metrics.Bind(owner);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root, ["Vigiloom:Agent:Server"] = owner.Server
            }).Build();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task<(string OperatingSystem, BaselineObservation Result)> Collect(BaselineCheck check, CancellationToken cancellationToken)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                throw new InvalidOperationException("Unexpected completion.");
            }
            using var worker = new BaselineWorker(runtime, metrics, new SamplingBudgetCoordinator(clock), config,
                clock, NullLogger<BaselineWorker>.Instance, new ResourceMonitor(), Collect);
            try
            {
                stopped = false;
                await worker.StartAsync(token);
                await started.Task.WaitAsync(token);
                clock.Advance(TimeSpan.FromSeconds(30));
                await cancelled.Task.WaitAsync(token);
                Assert.IsNull(await new DurableBatchQueue(vault, owner.AgentId).PeekAsync(token));
            }
            finally
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await worker.StopAsync(timeout.Token);
                if (worker.ExecuteTask is { } task) await task.WaitAsync(timeout.Token);
                stopped = true;
            }
        }
        finally
        {
            if (!stopped) throw new InvalidOperationException($"Worker did not stop; test directory retained: {root}");
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Worker_QueuesObservationUsingCurrentOwnerAndPolicy()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomBaselineWorkerTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        var stopped = true;
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", vault.StateDirectory);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = owner.Server, AgentId = owner.AgentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var selection = new BaselineSelection(Guid.CreateVersion7(), Guid.CreateVersion7(), "AAAAAAAAAAA=",
                new(BaselineSources.LinuxShadowCheck, "Shadow", "linux", ["ubuntu:24.04"], BaselineSources.LinuxShadowOperation,
                    BaselineSources.LinuxShadowTarget, "0640-or-stricter", "https://example.test/rule", "BSD-3-Clause"));
            var snapshot = BaselineSettingsSnapshot.Create(21600, [selection]);
            var runtime = new AgentBaselineSettingsRuntime();
            runtime.Bind(owner);
            runtime.Publish(owner, new(snapshot, DateTimeOffset.UtcNow, Guid.CreateVersion7(), owner.AgentId, owner.Server));
            var metrics = new AgentMetricSettingsRuntime();
            metrics.Bind(owner);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root, ["Vigiloom:Agent:Server"] = owner.Server
            }).Build();
            Task<(string OperatingSystem, BaselineObservation Result)> Collect(BaselineCheck check, CancellationToken cancellationToken) =>
                Task.FromResult(("ubuntu:24.04", new BaselineObservation(BaselineOutcome.Pass, "mode 600")));
            var monitor = new ResourceMonitor();
            using var worker = new BaselineWorker(runtime, metrics, new SamplingBudgetCoordinator(TimeProvider.System), config,
                TimeProvider.System, NullLogger<BaselineWorker>.Instance, monitor, Collect);
            try
            {
                stopped = false;
                await worker.StartAsync(token);
                var queue = new DurableBatchQueue(vault, owner.AgentId);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                await monitor.CollectionFinished.Task.WaitAsync(deadline.Token);
                var queued = await queue.PeekAsync(deadline.Token);
                Assert.IsNotNull(queued);
                Assert.IsTrue(BaselineBatchCodec.TryRead(queued.Payload, out var batch));
                Assert.IsNotNull(batch);
                var item = batch.Items.Single();
                Assert.AreEqual(selection.SourceId, item.SourceId);
                Assert.AreEqual(selection.RevisionId, item.RevisionId);
                Assert.AreEqual(BaselineOutcome.Pass, item.Outcome);
            }
            finally
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await worker.StopAsync(timeout.Token);
                if (worker.ExecuteTask is { } task) await task.WaitAsync(timeout.Token);
                stopped = true;
            }
        }
        finally
        {
            if (!stopped) throw new InvalidOperationException($"Worker did not stop; test directory retained: {root}");
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ResourceMonitor : IMetricResourceMonitor
    {
        private int reads;
        public TaskCompletionSource CollectionFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long ReadWorkingSetBytes() => 1024;
        public TimeSpan ReadCpuTime() => TimeSpan.Zero;
        public ValueTask<ProcessIoSnapshot> ReadIoAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref reads) == 2) CollectionFinished.TrySetResult();
            return ValueTask.FromResult(new ProcessIoSnapshot(0, 0));
        }
        public void Dispose() { }
    }
}

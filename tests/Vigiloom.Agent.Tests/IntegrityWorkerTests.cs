using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Integrity;
using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting.Integrity;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class IntegrityWorkerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Worker_AppliesEmptyPolicyWithoutStartingScan()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegrityWorkerTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        var stopped = true;
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", vault.StateDirectory);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = owner.Server, AgentId = owner.AgentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var snapshot = IntegritySettingsSnapshot.Create([], 0);
            var saved = await new AgentIntegritySettingsStore(vault, new Uri(owner.Server), owner.AgentId)
                .SaveAsync(snapshot, null, DateTimeOffset.UtcNow, token);
            var runtime = new AgentIntegritySettingsRuntime();
            runtime.Bind(owner);
            runtime.Publish(owner, saved);
            var metrics = new AgentMetricSettingsRuntime();
            metrics.Bind(owner);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = vault.StateDirectory, ["Vigiloom:Agent:Server"] = owner.Server
            }).Build();
            using var http = new HttpClient(new StatusHandler());
            var collected = 0;
            using var worker = new IntegrityWorker(new IntegrityStatusClient(http, TimeProvider.System), runtime, metrics,
                new SamplingBudgetCoordinator(TimeProvider.System), config, TimeProvider.System,
                NullLogger<IntegrityWorker>.Instance, new ResourceMonitor(false), (_, _) =>
                { Interlocked.Increment(ref collected); return Task.FromResult(new IntegrityReadResult(IntegrityScanOutcome.Complete, [], false)); });
            try
            {
                stopped = false;
                await worker.StartAsync(token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                while (runtime.Report(owner)?.Report.AppliedRevision is null) await Task.Delay(50, deadline.Token);
                Assert.AreEqual(snapshot.Revision, runtime.Report(owner)!.Value.Report.AppliedRevision);
                Assert.AreEqual(0, Volatile.Read(ref collected));
                Assert.IsNull(await new DurableBatchQueue(vault, owner.AgentId).ReadOutstandingIntegrityAsync(owner, deadline.Token));
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
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Worker_QueuesSnapshotUnderOwnerPolicyAndMarksCoverageGap(bool firstScopeFails)
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegrityWorkerTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        var stopped = true;
        Exception? failure = null;
        try
        {
            var token = TestContext.CancellationToken;
            var vault = new IdentityVault(root);
            var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", vault.StateDirectory);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = owner.Server, AgentId = owner.AgentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var scope = new IntegrityScope(Guid.CreateVersion7(), 1, IntegrityScopeKind.Directory, "/srv/app", true,
                ImmutableArray<string>.Empty, 60, 100, 1024, 4096, 4);
            var second = scope with { Id = Guid.CreateVersion7(), Root = "/srv/other" };
            var scopes = new[] { scope, second }.OrderBy(x => x.Id).ToArray();
            var expected = firstScopeFails ? scopes[1] : scope;
            var store = new AgentIntegritySettingsStore(vault, new Uri(owner.Server), owner.AgentId);
            var saved = await store.SaveAsync(IntegritySettingsSnapshot.Create(firstScopeFails ? scopes : [scope], 1), null, DateTimeOffset.UtcNow, token);
            var runtime = new AgentIntegritySettingsRuntime();
            runtime.Bind(owner);
            runtime.Publish(owner, saved);
            var metrics = new AgentMetricSettingsRuntime();
            metrics.Bind(owner);
            var metricPolicy = new AgentMetricSettings(10, 10, 100, 4096, 2000, 4096);
            metrics.Publish(owner, new(AgentMetricSettingsSnapshot.Create(metricPolicy), DateTimeOffset.UtcNow,
                Guid.CreateVersion7(), owner.AgentId, owner.Server));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = vault.StateDirectory, ["Vigiloom:Agent:Server"] = owner.Server
            }).Build();
            using var http = new HttpClient(new StatusHandler());
            var collected = 0;
            var failures = 0;
            Task<IntegrityReadResult> Collect(IntegrityScope input, CancellationToken _)
            {
                if (firstScopeFails && input.Id == scopes[0].Id)
                {
                    Interlocked.Increment(ref failures);
                    throw new IOException("Synthetic unavailable scope.");
                }
                Assert.AreEqual(expected.Id, input.Id);
                Interlocked.Increment(ref collected);
                return Task.FromResult(new IntegrityReadResult(IntegrityScanOutcome.Complete, [], false));
            }
            var monitor = new ResourceMonitor(!firstScopeFails);
            using var worker = new IntegrityWorker(new IntegrityStatusClient(http, TimeProvider.System), runtime, metrics,
                new SamplingBudgetCoordinator(TimeProvider.System), config, TimeProvider.System,
                NullLogger<IntegrityWorker>.Instance, monitor, Collect);
            Exception? workFailure = null;
            try
            {
                stopped = false;
                await worker.StartAsync(token);
                var queue = new DurableBatchQueue(vault, owner.AgentId);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                while (await queue.ReadOutstandingIntegrityAsync(owner, deadline.Token) is null)
                    await Task.Delay(50, deadline.Token);
                Assert.AreEqual(1, Volatile.Read(ref collected));
                Assert.AreEqual(firstScopeFails ? 1 : 0, Volatile.Read(ref failures));
                Assert.IsNotNull(await queue.ReadIntegrityGapAsync(owner, expected.Id, expected.Revision, deadline.Token));
                Assert.IsGreaterThan(0, (await queue.GetStatusAsync(deadline.Token)).TotalBatches);
                var queued = await queue.PeekAsync(deadline.Token);
                Assert.IsNotNull(queued);
                Assert.IsTrue(IntegrityChunkCodec.TryRead(queued.Payload, out var chunk));
                Assert.IsTrue(chunk!.CoverageGap);
                Assert.IsGreaterThanOrEqualTo(4, monitor.CpuReads, "Collection and enqueue both account for CPU.");
            }
            catch (Exception ex) { workFailure = ex; throw; }
            finally
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await worker.StopAsync(timeout.Token);
                    if (worker.ExecuteTask is { } task) await task.WaitAsync(timeout.Token);
                    stopped = true;
                }
                catch (Exception stopError) when (workFailure is not null) { throw new AggregateException(workFailure, stopError); }
            }
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            try
            {
                if (!stopped) throw new InvalidOperationException($"Worker did not stop; test directory retained: {root}");
                var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup escaped its temporary root.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupError) when (failure is not null) { throw new AggregateException(failure, cleanupError); }
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var scopeId = Guid.Parse(request.RequestUri!.Segments[^1]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new IntegrityScopeStatus(
                scopeId, true, 1, true, 0, null, false, null, null, null, null, null, null)) });
        }
    }

    private sealed class ResourceMonitor(bool exhaust) : IMetricResourceMonitor
    {
        private int reads;
        public int CpuReads => Volatile.Read(ref reads);
        public long ReadWorkingSetBytes() => 1024;
        public TimeSpan ReadCpuTime() => TimeSpan.FromMilliseconds(Interlocked.Increment(ref reads) * (exhaust ? 200 : 0));
        public ValueTask<ProcessIoSnapshot> ReadIoAsync(CancellationToken token) => ValueTask.FromResult(new ProcessIoSnapshot(0, 0));
        public void Dispose() { }
    }
}

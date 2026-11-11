using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting.Inventory;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class InventoryWorkerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Schedule_UsesNewIntervalWithoutInterruptingAnActivePass()
    {
        var clock = new FakeTimeProvider();
        var schedule = new InventoryPassSchedule(clock);
        Assert.IsTrue(schedule.IsDue(21600));
        schedule.Begin();
        schedule.Complete();
        clock.Advance(TimeSpan.FromSeconds(1800));
        Assert.IsFalse(schedule.IsDue(3600));
        Assert.IsTrue(schedule.IsDue(900));
        schedule.Begin();
        Assert.IsTrue(schedule.IsDue(604800));
        schedule.Complete();
        Assert.IsFalse(schedule.IsDue(900));
        clock.Advance(TimeSpan.FromSeconds(900));
        Assert.IsTrue(schedule.IsDue(900));
        schedule.Reset();
        Assert.IsTrue(schedule.IsDue(604800));
    }

    [TestMethod]
    public void Settings_ReportOnlyPolicyObservedByWorkerAndKeepFailureSeparate()
    {
        var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", "state");
        var runtime = new InventorySettingsRuntime();
        runtime.Bind(owner);
        var first = InventorySettingsSnapshot.Create(new(3600));
        var second = InventorySettingsSnapshot.Create(new(7200));
        runtime.Publish(owner, new(first, DateTimeOffset.UtcNow, Guid.CreateVersion7(), owner.AgentId, owner.Server));
        Assert.IsNull(runtime.Report(owner)!.Report.Applied);
        var appliedAt = DateTimeOffset.UtcNow;
        Assert.AreEqual(3600, runtime.ApplyForOwner(owner, appliedAt)!.ScanIntervalSeconds);
        runtime.Publish(owner, new(second, DateTimeOffset.UtcNow, Guid.CreateVersion7(), owner.AgentId, owner.Server));
        Assert.AreEqual(first, runtime.Report(owner)!.Report.Applied);
        runtime.MarkFailure(owner, AgentConfigurationFailure.Unavailable);
        Assert.AreEqual(AgentConfigurationFailure.Unavailable, runtime.Report(owner)!.Report.Failure);
        Assert.AreEqual(7200, runtime.ApplyForOwner(owner, appliedAt.AddSeconds(1))!.ScanIntervalSeconds);
        Assert.AreEqual(second, runtime.Report(owner)!.Report.Applied);
        Assert.IsNull(runtime.Report(new AgentSettingsOwner(Guid.CreateVersion7(), owner.Server, owner.StateDirectory)));
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Worker_FinishesEnqueueAfterCollectionExhaustsCpuBudget()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryWorkerTests");
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
            var settings = new AgentMetricSettingsRuntime();
            settings.Bind(owner);
            var inventorySettings = new InventorySettingsRuntime();
            inventorySettings.Bind(owner);
            var policy = new AgentMetricSettings(10, 10, 100, 4096, 2000, 4096);
            settings.Publish(owner, new AgentSavedMetricSettings(AgentMetricSettingsSnapshot.Create(policy),
                DateTimeOffset.UtcNow, Guid.CreateVersion7(), owner.AgentId, owner.Server));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = vault.StateDirectory, ["Vigiloom:Agent:Server"] = owner.Server
            }).Build();
            using var http = new HttpClient(new EmptyStatusHandler());
            var monitor = new ExhaustedMonitor();
            var collected = 0;
            Task<InventoryReadResult<InventoryEntry>> Collect(InventorySource source, CancellationToken _)
            {
                Interlocked.Increment(ref collected);
                return Task.FromResult(new InventoryReadResult<InventoryEntry>(InventoryReadStatus.Error, []));
            }
            using var worker = new InventoryWorker(new InventoryStatusClient(http, TimeProvider.System), settings, inventorySettings,
                new SamplingBudgetCoordinator(TimeProvider.System), config, TimeProvider.System,
                NullLogger<InventoryWorker>.Instance, monitor, Collect);
            try
            {
                stopped = false;
                await worker.StartAsync(token);
                var queue = new DurableBatchQueue(vault, owner.AgentId);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                while (true)
                {
                    try { if ((await queue.GetStatusAsync(deadline.Token)).TotalBatches > 0) break; }
                    catch (IOException) { }
                    await Task.Delay(50, deadline.Token);
                }
                Assert.AreEqual(1, Volatile.Read(ref collected));
                Assert.IsGreaterThanOrEqualTo(4, monitor.CpuReads);
            }
            finally
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await worker.StopAsync(timeout.Token);
                if (worker.ExecuteTask is { } task) await task.WaitAsync(timeout.Token);
                stopped = true;
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

    private sealed class EmptyStatusHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
    }

    private sealed class ExhaustedMonitor : IMetricResourceMonitor
    {
        private int cpuReads;
        public int CpuReads => Volatile.Read(ref cpuReads);
        public long ReadWorkingSetBytes() => 1024;
        public TimeSpan ReadCpuTime() => TimeSpan.FromMilliseconds(Interlocked.Increment(ref cpuReads) * 200);
        public ValueTask<ProcessIoSnapshot> ReadIoAsync(CancellationToken token) => ValueTask.FromResult(new ProcessIoSnapshot(0, 0));
        public void Dispose() { }
    }
}

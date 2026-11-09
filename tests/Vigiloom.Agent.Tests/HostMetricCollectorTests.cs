using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class HostMetricCollectorTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task CollectAsync_WindowsEmitsCpuCoreValuesAfterWarmup()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        using var collector = new HostMetricCollector(TimeProvider.System);
        var first = await collector.CollectAsync(TimeSpan.FromMinutes(1), TestContext.CancellationToken, includeProcesses: false);
        Assert.IsFalse(first.Points.Any(x => x.Kind == AgentMetricKind.CpuCorePercent));
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.CancellationToken);
        var second = await collector.CollectAsync(TimeSpan.FromMinutes(1), TestContext.CancellationToken, includeProcesses: false);
        Assert.IsTrue(second.Points.Any(x => x.Kind == AgentMetricKind.CpuCorePercent));
        Assert.IsTrue(second.Points.Any(x => x.Kind == AgentMetricKind.CpuTotalPercent));
    }

    [TestMethod]
    public async Task CollectAsync_RealHostProducesValidMemoryAndReportsUnimplementedSources()
    {
        using var collector = new HostMetricCollector(TimeProvider.System);
        var result = await collector.CollectAsync(TimeSpan.FromMinutes(2), TestContext.CancellationToken);
        Assert.IsTrue(result.Points.Any(x => x.Kind == AgentMetricKind.MemoryTotalBytes && x.Value > 0));
        if (OperatingSystem.IsWindows() && !result.UnavailableSources.Contains("pagefile"))
        {
            var total = result.Points.Single(x => x.Kind == AgentMetricKind.PageFileTotalBytes);
            var used = result.Points.Single(x => x.Kind == AgentMetricKind.PageFileUsedBytes);
            Assert.IsLessThanOrEqualTo(total.Value, used.Value);
        }
        Assert.Contains("network", result.UnavailableSources);
        Assert.IsTrue(result.UnavailableSources.Contains("volume") || result.Points.Any(x => x.Kind == AgentMetricKind.VolumeTotalBytes));
        Assert.Contains("disk_io", result.UnavailableSources);
        Assert.IsTrue(result.UnavailableSources.Contains("process") || result.Points.Any(x => x.Kind == AgentMetricKind.ProcessWorkingSetBytes));
        foreach (var chunk in result.Points.Chunk(AgentBatchCodec.MaximumItems))
        {
            var batch = new AgentMetricBatch(AgentMetricBatchCodec.SchemaVersion, Guid.CreateVersion7(), DateTimeOffset.UtcNow, chunk);
            var bytes = AgentMetricBatchCodec.Serialize(batch);
            Assert.IsTrue(AgentMetricBatchCodec.TryRead(bytes, out _));
        }
    }

    [TestMethod]
    public async Task CollectAsync_ProcessSamplingCanBeSkippedWithoutReportingFailure()
    {
        using var collector = new HostMetricCollector(TimeProvider.System);
        var result = await collector.CollectAsync(TimeSpan.FromMinutes(2), TestContext.CancellationToken, includeProcesses: false);
        Assert.IsFalse(result.Points.Any(x => x.Kind is AgentMetricKind.ProcessCpuPercent or AgentMetricKind.ProcessWorkingSetBytes));
        Assert.DoesNotContain("process", result.UnavailableSources);
        Assert.IsTrue(result.Points.Any(x => x.Kind == AgentMetricKind.MemoryTotalBytes));
    }

    [TestMethod]
    public void CollectProcesses_CanRunWithoutSamplingOtherSources()
    {
        using var collector = new HostMetricCollector(TimeProvider.System);
        var result = collector.CollectProcesses(TimeSpan.FromMinutes(2), TestContext.CancellationToken);
        Assert.IsTrue(result.UnavailableSources.All(x => x == "process"));
        Assert.IsTrue(result.Points.All(x => x.Kind is AgentMetricKind.ProcessCpuPercent or AgentMetricKind.ProcessWorkingSetBytes));
        Assert.IsTrue(result.UnavailableSources.Contains("process") || result.Points.Any(x => x.Kind == AgentMetricKind.ProcessWorkingSetBytes));
    }

    [TestMethod]
    public async Task CollectAsync_AlreadyCancelledDoesNotCollect()
    {
        using var cancelled = new CancellationTokenSource();
        using var collector = new HostMetricCollector(TimeProvider.System);
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            collector.CollectAsync(TimeSpan.FromMinutes(2), cancelled.Token));
    }
}

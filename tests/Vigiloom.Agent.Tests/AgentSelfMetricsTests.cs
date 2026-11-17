using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class AgentSelfMetricsTests
{
    [TestMethod]
    public void ReportsMemoryAndCpuFromConsecutiveSamples()
    {
        var clock = new FakeTimeProvider();
        using var resources = new Counters { Cpu = TimeSpan.FromSeconds(1), Memory = 1000 };
        var metrics = new AgentSelfMetrics(resources, clock);
        var identity = new AgentRuntimeReport("1.0.0", "win-x64");
        var first = metrics.Read(identity)!;
        Assert.IsNull(first.CpuPercent);
        Assert.AreEqual(1000, first.WorkingSetBytes);
        clock.Advance(TimeSpan.FromSeconds(2));
        resources.Cpu = TimeSpan.FromSeconds(2);
        resources.Memory = 2000;
        var second = metrics.Read(identity)!;
        Assert.AreEqual(50, second.CpuPercent);
        Assert.AreEqual(2000, second.WorkingSetBytes);
        Assert.IsTrue(second.IsValid());
    }

    [TestMethod]
    public void UnavailableCountersDoNotReportZeroOrReuseOldCpu()
    {
        var clock = new FakeTimeProvider();
        using var resources = new Counters { Cpu = TimeSpan.FromSeconds(1), Memory = 1000 };
        var metrics = new AgentSelfMetrics(resources, clock);
        var identity = new AgentRuntimeReport("1.0.0", "linux-x64");
        metrics.Read(identity);
        resources.Unavailable = true;
        Assert.IsNull(metrics.Read(identity)!.WorkingSetBytes);
        resources.Unavailable = false;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.IsNull(metrics.Read(identity)!.CpuPercent);
    }

    private sealed class Counters : IMetricResourceMonitor
    {
        public TimeSpan Cpu { get; set; }
        public long Memory { get; set; }
        public bool Unavailable { get; set; }
        public long ReadWorkingSetBytes() => Memory;
        public TimeSpan ReadCpuTime() => Unavailable ? throw new InvalidOperationException() : Cpu;
        public ValueTask<ProcessIoSnapshot> ReadIoAsync(CancellationToken token) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

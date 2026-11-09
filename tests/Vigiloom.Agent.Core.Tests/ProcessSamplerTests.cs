using System.Diagnostics;
using Vigiloom.Agent.Core.Metrics;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class ProcessSamplerTests
{
    [TestMethod]
    public void Update_UsesCpuTimeAndDistinguishesPidReuse()
    {
        var clock = TimeProvider.System;
        var tracker = new ProcessRateTracker(clock);
        var start = clock.GetTimestamp();
        var tick = clock.TimestampFrequency;
        var process = new ProcessCounters(7, 100, TimeSpan.FromSeconds(1), 1000);
        Assert.IsNull(tracker.Update([process], start, TimeSpan.FromSeconds(5))[0].CpuPercent);
        var next = tracker.Update([process with { CpuTime = TimeSpan.FromSeconds(4) }], start + 2 * tick, TimeSpan.FromSeconds(5));
        Assert.AreEqual(150d, next[0].CpuPercent);
        var reused = tracker.Update([process with { StartedUtcTicks = 200 }], start + 3 * tick, TimeSpan.FromSeconds(5));
        Assert.IsNull(reused[0].CpuPercent);
    }

    [TestMethod]
    public void Update_ResetMissingAndStaleIntervalsDoNotCreateRates()
    {
        var clock = TimeProvider.System;
        var tracker = new ProcessRateTracker(clock);
        var start = clock.GetTimestamp();
        var tick = clock.TimestampFrequency;
        var process = new ProcessCounters(7, 100, TimeSpan.FromSeconds(2), 1000);
        tracker.Update([process], start, TimeSpan.FromSeconds(3));
        Assert.IsNull(tracker.Update([process with { CpuTime = TimeSpan.Zero }], start + tick, TimeSpan.FromSeconds(3))[0].CpuPercent);
        tracker.Update([], start + 2 * tick, TimeSpan.FromSeconds(3));
        Assert.IsNull(tracker.Update([process], start + 3 * tick, TimeSpan.FromSeconds(3))[0].CpuPercent);
        Assert.IsNull(tracker.Update([process], start + 7 * tick, TimeSpan.FromSeconds(3))[0].CpuPercent);
        tracker.Reset();
        Assert.IsNull(tracker.Update([process], start + 8 * tick, TimeSpan.FromSeconds(3))[0].CpuPercent);
    }

    [TestMethod]
    public void Sample_ReportsActualCurrentProcessWhenWithinLimit()
    {
        var sampler = new ProcessSampler(TimeProvider.System);
        var sample = sampler.Sample(TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.IsLessThanOrEqualTo(ProcessSampler.MaximumProcesses, sample.Processes.Count);
        using var self = Process.GetCurrentProcess();
        if (!sample.Truncated && !sample.BudgetExhausted)
        {
            var current = sample.Processes.Single(p => p.Counters.Id == self.Id);
            Assert.IsGreaterThan(0L, current.Counters.WorkingSetBytes);
            Assert.IsNull(current.CpuPercent);
        }
    }

    [TestMethod]
    public void Sample_ReportsBudgetExhaustionWithoutInventingValues()
    {
        var sampler = new ProcessSampler(new AdvancingClock());
        var result = sampler.Sample(TimeSpan.FromMinutes(1), CancellationToken.None, TimeSpan.FromSeconds(1));
        Assert.IsTrue(result.BudgetExhausted);
        Assert.IsEmpty(result.Processes);
        Assert.AreEqual(0, result.UnavailableProcesses);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => sampler.Sample(TimeSpan.FromMinutes(1), CancellationToken.None, TimeSpan.Zero));
    }

    private sealed class AdvancingClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Add(ref ticks, 1000);
    }
}

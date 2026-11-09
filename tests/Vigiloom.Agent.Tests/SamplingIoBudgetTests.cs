using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Agent.Hosting.Metrics;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class SamplingIoBudgetTests
{
    [TestMethod]
    public void Budget_TracksBothDirectionsAndAdaptsWithoutResettingUsage()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var budget = new SamplingIoBudget(clock, TimeSpan.FromMinutes(1), 100);
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
        budget.Record(new(10, 20), new(50, 50));
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
        budget.SetAllowance(70);
        Assert.AreEqual(TimeSpan.FromMinutes(1), budget.Delay());
        budget.SetAllowance(101);
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
        clock.Advance(TimeSpan.FromSeconds(61));
        budget.Record(new(0, 0), new(101, 0));
        Assert.AreEqual(TimeSpan.FromMinutes(1), budget.Delay());
    }

    [TestMethod]
    public void Budget_RejectsCounterResetAndSaturatesOverflow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var budget = new SamplingIoBudget(clock, TimeSpan.FromMinutes(1), 100);
        Assert.ThrowsExactly<InvalidOperationException>(() => budget.Record(new(10, 2), new(9, 3)));
        budget.Record(new(0, 0), new(ulong.MaxValue, 1));
        Assert.AreEqual(TimeSpan.FromMinutes(1), budget.Delay());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => budget.SetAllowance(0));
    }

    [TestMethod]
    public void UnknownObservation_PausesCollectionUntilNextWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var budget = new SamplingIoBudget(clock, TimeSpan.FromMinutes(1), 100);
        budget.MarkUnknown();
        Assert.AreEqual(TimeSpan.FromMinutes(1), budget.Delay());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
    }
}

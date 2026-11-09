using Microsoft.Extensions.Time.Testing;
using Vigiloom.Agent.Hosting.Metrics;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class SamplingCpuBudgetTests
{
    [TestMethod]
    public void ExhaustedBudget_DelaysUntilNextWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var budget = new SamplingCpuBudget(clock, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3));
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
        budget.Record(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(7));
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
        clock.Advance(TimeSpan.FromSeconds(10));
        budget.Record(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(8));
        Assert.AreEqual(TimeSpan.FromSeconds(50), budget.Delay());
        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
    }

    [TestMethod]
    public void CounterReset_IsRejectedInsteadOfCreatingCredit()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var budget = new SamplingCpuBudget(clock, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3));
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
        Assert.ThrowsExactly<InvalidOperationException>(() => budget.Record(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4)));
    }

    [TestMethod]
    public void CycleCrossesWindow_CostIsChargedToCompletionWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var budget = new SamplingCpuBudget(clock, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3));
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
        clock.Advance(TimeSpan.FromSeconds(61));
        budget.Record(TimeSpan.Zero, TimeSpan.FromSeconds(3));
        Assert.AreEqual(TimeSpan.FromMinutes(1), budget.Delay());
    }

    [TestMethod]
    public void ChangingAllowance_PreservesAlreadyConsumedCpu()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var budget = new SamplingCpuBudget(clock, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3));
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
        budget.Record(TimeSpan.Zero, TimeSpan.FromSeconds(2));
        budget.SetAllowance(TimeSpan.FromSeconds(1));
        Assert.AreEqual(TimeSpan.FromMinutes(1), budget.Delay());
        budget.SetAllowance(TimeSpan.FromSeconds(4));
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => budget.SetAllowance(TimeSpan.Zero));
    }

    [TestMethod]
    public void UnknownObservation_PausesCollectionUntilNextWindow()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var budget = new SamplingCpuBudget(clock, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(3));
        budget.MarkUnknown();
        Assert.AreEqual(TimeSpan.FromMinutes(1), budget.Delay());
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.AreEqual(TimeSpan.Zero, budget.Delay());
    }
}

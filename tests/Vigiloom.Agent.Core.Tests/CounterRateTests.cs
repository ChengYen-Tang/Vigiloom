using Vigiloom.Agent.Core.Metrics;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class CounterRateTests
{
    [TestMethod]
    public void PerSecond_UsesActualElapsedTimeAndPreservesZero()
    {
        Assert.AreEqual(20d, CounterRate.PerSecond(100, 150, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(30)));
        Assert.AreEqual(0d, CounterRate.PerSecond(100, 100, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)));
    }

    [TestMethod]
    public void PerSecond_MissingResetOrStaleSamplesDoNotProduceRates()
    {
        var limit = TimeSpan.FromSeconds(30);
        Assert.IsNull(CounterRate.PerSecond(null, 10, TimeSpan.FromSeconds(1), limit));
        Assert.IsNull(CounterRate.PerSecond(ulong.MaxValue, 0, TimeSpan.FromSeconds(1), limit));
        Assert.IsNull(CounterRate.PerSecond(10, 11, TimeSpan.Zero, limit));
        Assert.IsNull(CounterRate.PerSecond(10, 11, TimeSpan.FromSeconds(-1), limit));
        Assert.IsNull(CounterRate.PerSecond(10, 11, TimeSpan.FromSeconds(31), limit));
        Assert.AreEqual(1d, CounterRate.PerSecond(10, 40, limit, limit));
    }

    [TestMethod]
    public void PerSecond_RejectsNonpositiveMaximumInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CounterRate.PerSecond(1, 2, TimeSpan.FromSeconds(1), TimeSpan.Zero));
    }
}

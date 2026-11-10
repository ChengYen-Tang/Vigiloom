using Vigiloom.Agent.Hosting.Metrics;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class SamplingMemoryBudgetTests
{
    [TestMethod]
    public void Pressure_ChangesAtConfiguredThreshold()
    {
        Assert.IsFalse(SamplingMemoryBudget.IsExceeded(99, 100));
        Assert.IsTrue(SamplingMemoryBudget.IsExceeded(100, 100));
        Assert.IsTrue(SamplingMemoryBudget.IsExceeded(101, 100));
        Assert.IsFalse(SamplingMemoryBudget.IsExceeded(101, 200));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SamplingMemoryBudget.IsExceeded(-1, 100));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SamplingMemoryBudget.IsExceeded(1, 0));
    }
}

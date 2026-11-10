using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class MetricWindowTests
{
    [TestMethod]
    public void SamplesAndDeltaEndpointsUseTheirRespectiveBoundarySemantics()
    {
        const ulong boundary = 600_000_000_000;
        Assert.IsTrue(MetricWindow.TryForSample(boundary, out var sample));
        Assert.AreEqual(boundary, sample.Start);
        Assert.AreEqual(900_000_000_000UL, sample.End);
        Assert.IsTrue(MetricWindow.TryForDeltaEnd(boundary, out var delta));
        Assert.AreEqual(300_000_000_000UL, delta.Start);
        Assert.AreEqual(boundary, delta.End);
        Assert.IsTrue(MetricWindow.TryForDeltaEnd(boundary + 1, out var next));
        Assert.AreEqual(boundary, next.Start);
    }

    [TestMethod]
    public void MissingAndOverflowingTimestampsAreRejected()
    {
        Assert.IsFalse(MetricWindow.TryForDeltaEnd(0, out _));
        Assert.IsFalse(MetricWindow.TryForSample(0, out _));
        Assert.IsFalse(MetricWindow.TryForSample(ulong.MaxValue, out _));
    }
}

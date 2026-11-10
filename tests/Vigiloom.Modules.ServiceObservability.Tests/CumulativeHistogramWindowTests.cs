using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class CumulativeHistogramWindowTests
{
    [TestMethod]
    public void PreservesSnapshotsPerResetEpoch()
    {
        var a = Point(50, 110, 1, 0);
        var b = Point(50, 150, 1, 1);
        var c = Point(160, 180, 0, 1);
        var result = CumulativeHistogramWindow.Reduce([a, b, c], 100, 200);
        Assert.HasCount(2, result.Epochs);
        Assert.AreEqual(1UL, result.Epochs[0].First!.Count);
        Assert.AreEqual(2UL, result.Epochs[0].Last!.Count);
        Assert.AreEqual(1UL, result.Epochs[1].Last!.Count);
        Assert.IsFalse(result.DistributionIncompatible);
        a.BucketCounts[0] = 99;
        Assert.AreEqual(1UL, result.Epochs[0].First!.BucketCounts[0]);
    }

    [TestMethod]
    public void DetectsDecreaseAndBoundChangeWithinEpoch()
    {
        var a = Point(50, 110, 2, 0);
        var b = Point(50, 150, 1, 2);
        var decreased = CumulativeHistogramWindow.Reduce([a, b], 100, 200);
        Assert.IsTrue(decreased.DistributionIncompatible);
        Assert.IsFalse(decreased.CountDecreased);
        b.ExplicitBounds[0] = 10;
        var changed = CumulativeHistogramWindow.Reduce([a, b], 100, 200);
        Assert.IsTrue(changed.DistributionIncompatible);
    }

    [TestMethod]
    public void IntermediateCumulativeMinimumCannotIncrease()
    {
        var a = Point(50, 110, 1, 1); a.Sum = 7; a.Min = 1; a.Max = 6;
        var b = Point(50, 150, 2, 1); b.Sum = 10; b.Min = 2; b.Max = 6;
        var c = Point(50, 190, 3, 1); c.Sum = 11; c.Min = 1; c.Max = 6;
        var result = CumulativeHistogramWindow.Reduce([a, b, c], 100, 200);
        Assert.AreEqual(0, result.Invalid);
        Assert.IsTrue(result.DistributionIncompatible);
    }

    private static HistogramDataPoint Point(ulong start, ulong time, ulong low, ulong high) => new()
    {
        StartTimeUnixNano = start, TimeUnixNano = time, Count = low + high,
        ExplicitBounds = { 5 }, BucketCounts = { low, high }
    };
}

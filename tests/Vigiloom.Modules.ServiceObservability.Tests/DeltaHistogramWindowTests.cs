using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class DeltaHistogramWindowTests
{
    [TestMethod]
    public void CompatibleCompleteIntervalsCombineBucketsAndOptionalStatistics()
    {
        var first = Point(100, 200, 3, [3, 0], [5], 6);
        first.Min = 1; first.Max = 4;
        first.Exemplars.Add(new Exemplar());
        var second = Point(200, 300, 4, [3, 1], [5], 11);
        second.Max = 6;
        var result = DeltaHistogramWindow.Reduce([second, first], 100, 300);
        Assert.IsNotNull(result.Aggregate);
        Assert.AreEqual(7UL, result.Aggregate.Count);
        CollectionAssert.AreEqual(new ulong[] { 6, 1 }, result.Aggregate.BucketCounts.ToArray());
        Assert.AreEqual(17.0, result.Aggregate.Sum);
        Assert.IsFalse(result.Aggregate.HasMin);
        Assert.AreEqual(6.0, result.Aggregate.Max);
        Assert.IsTrue(result.ExemplarsOmitted);
        Assert.IsTrue(result.SumBoundsUnverified);
        Assert.IsEmpty(result.Aggregate.Exemplars);
    }

    [TestMethod]
    public void IncompatibleOverlappingOrMalformedIntervalsDoNotProduceTotals()
    {
        var first = Point(100, 200, 1, [1, 0], [5], 1);
        var differentBounds = Point(200, 300, 1, [1, 0], [7], 1);
        var incompatible = DeltaHistogramWindow.Reduce([first, differentBounds], 100, 300);
        Assert.IsTrue(incompatible.IncompatibleBounds);
        Assert.IsNull(incompatible.Aggregate);
        var overlapping = DeltaHistogramWindow.Reduce([first, Point(150, 300, 1, [1, 0], [5], 1)], 100, 300);
        Assert.IsTrue(overlapping.Overlap);
        Assert.IsNull(overlapping.Aggregate);
        var badCount = DeltaHistogramWindow.Reduce([Point(100, 300, 4, [1, 0], [5], 1)], 100, 300);
        Assert.AreEqual(1, badCount.Invalid);
        Assert.IsNull(badCount.Aggregate);
        var impossibleMax = Point(100, 300, 3, [1, 2], [5], 6);
        impossibleMax.Max = 4;
        var impossible = DeltaHistogramWindow.Reduce([impossibleMax], 100, 300);
        Assert.AreEqual(1, impossible.Invalid);
        Assert.IsNull(impossible.Aggregate);
        var emptyWithMin = Point(100, 300, 0, [], [], 0);
        emptyWithMin.Min = -999;
        Assert.AreEqual(1, DeltaHistogramWindow.Reduce([emptyWithMin], 100, 300).Invalid);
        var inconsistentSingle = Point(100, 300, 1, [1, 0], [5], 100);
        inconsistentSingle.Min = 2; inconsistentSingle.Max = 2;
        Assert.AreEqual(1, DeltaHistogramWindow.Reduce([inconsistentSingle], 100, 300).Invalid);
        var inconsistentMultiple = Point(100, 300, 2, [2, 0], [5], 100);
        inconsistentMultiple.Min = 2; inconsistentMultiple.Max = 2;
        Assert.AreEqual(1, DeltaHistogramWindow.Reduce([inconsistentMultiple], 100, 300).Invalid);
        var sumOutsideBucket = Point(100, 300, 1, [1, 0], [5], 100);
        Assert.AreEqual(1, DeltaHistogramWindow.Reduce([sumOutsideBucket], 100, 300).Invalid);
        var inconsistentTwo = Point(100, 300, 2, [], [], 0);
        inconsistentTwo.Min = 0; inconsistentTwo.Max = 100;
        Assert.AreEqual(1, DeltaHistogramWindow.Reduce([inconsistentTwo], 100, 300).Invalid);
        var rounded = Point(100, 300, 3, [], [], 0.30000000000000004);
        rounded.Min = 0.1; rounded.Max = 0.1;
        Assert.IsNotNull(DeltaHistogramWindow.Reduce([rounded], 100, 300).Aggregate);
    }

    [TestMethod]
    public void HistogramSumsAvoidLossDuringFloatingCancellation()
    {
        var result = DeltaHistogramWindow.Reduce([
            Point(100, 200, 1, [], [], 10_000_000_000_000_000),
            Point(200, 300, 1, [], [], 1),
            Point(300, 400, 1, [], [], -10_000_000_000_000_000)
        ], 100, 400);
        Assert.IsNotNull(result.Aggregate);
        Assert.AreEqual(1.0, result.Aggregate.Sum);
    }

    private static HistogramDataPoint Point(ulong start, ulong end, ulong count, ulong[] buckets, double[] bounds, double sum)
    {
        var point = new HistogramDataPoint { StartTimeUnixNano = start, TimeUnixNano = end, Count = count, Sum = sum };
        point.BucketCounts.Add(buckets);
        point.ExplicitBounds.Add(bounds);
        return point;
    }
}

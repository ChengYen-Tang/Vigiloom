using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class SummaryWindowTests
{
    [TestMethod]
    public void KeepsSnapshotsAndResetEpochsWithoutCombiningQuantiles()
    {
        var first = Point(50, 110, 2, 3, 1, 2);
        var last = Point(50, 120, 2, 5, 2, 3);
        var reset = Point(125, 130, 1, 4, 4, 4);
        var result = SummaryWindow.Reduce([first, last, reset], 100, 200);
        Assert.HasCount(2, result.Epochs);
        var epoch = result.Epochs.Single(x => x.Start == 50);
        Assert.AreEqual(1d, epoch.First!.QuantileValues[0].Value);
        Assert.AreEqual(3d, epoch.Last!.QuantileValues[1].Value);
        Assert.AreEqual(4d, result.Epochs.Single(x => x.Start == 125).Last!.Sum);
        first.Sum = 99;
        Assert.AreEqual(3d, epoch.First.Sum);
        Assert.IsFalse(result.OverlappingEpochs);
    }

    [TestMethod]
    public void MissingAndAmbiguousSnapshotsDoNotHideGaps()
    {
        var result = SummaryWindow.Reduce([
            Point(50, 110, 2, 3, 1, 2),
            new SummaryDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 120, Flags = 1 },
            Point(50, 130, 1, 2, 2, 2)
        ], 100, 200);
        Assert.AreEqual(1, result.Missing);
        Assert.IsTrue(result.CountDecreased);
        var tied = SummaryWindow.Reduce([Point(50, 110, 1, 1, 1, 1), Point(50, 110, 1, 2, 2, 2)], 100, 200);
        Assert.IsTrue(tied.AmbiguousTimestamps);
        Assert.IsNull(tied.Epochs.Single().First);
        Assert.IsNull(tied.Epochs.Single().Last);
    }

    [TestMethod]
    public void InvalidAndUnverifiableStatisticsAreDistinguished()
    {
        var invalid = Point(50, 110, 1, double.NaN, 3, 4);
        var incomplete = new SummaryDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 120, Count = 3, Sum = 6 };
        incomplete.QuantileValues.Add(new SummaryDataPoint.Types.ValueAtQuantile { Quantile = .5, Value = 2 });
        var result = SummaryWindow.Reduce([invalid, incomplete], 100, 200);
        Assert.AreEqual(1, result.Invalid);
        Assert.IsTrue(result.StatisticsUnverified);
        Assert.IsNull(result.Epochs.Single().First);
    }

    [TestMethod]
    public void RecentQuantilesAreNotValidatedAgainstCumulativePopulation()
    {
        var point = Point(50, 110, 2, 10001, 1, 1);
        var result = SummaryWindow.Reduce([point], 100, 200);
        Assert.AreEqual(0, result.Invalid);
        Assert.IsTrue(result.StatisticsUnverified);
        Assert.AreEqual(10001d, result.Epochs.Single().Last!.Sum);
    }

    private static SummaryDataPoint Point(ulong start, ulong time, ulong count, double sum, double minimum, double maximum)
    {
        var point = new SummaryDataPoint { StartTimeUnixNano = start, TimeUnixNano = time, Count = count, Sum = sum };
        point.QuantileValues.Add(new SummaryDataPoint.Types.ValueAtQuantile { Quantile = 0, Value = minimum });
        point.QuantileValues.Add(new SummaryDataPoint.Types.ValueAtQuantile { Quantile = 1, Value = maximum });
        return point;
    }
}

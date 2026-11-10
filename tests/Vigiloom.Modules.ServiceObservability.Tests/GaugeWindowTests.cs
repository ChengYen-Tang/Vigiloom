using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class GaugeWindowTests
{
    [TestMethod]
    public void LatestSamplePreservesIntegerPrecisionAndNanosecondOrder()
    {
        var result = GaugeWindow.Reduce([
            new NumberDataPoint { TimeUnixNano = 199, AsInt = 9_007_199_254_740_993 },
            new NumberDataPoint { TimeUnixNano = 198, AsDouble = 1.5 }
        ], 100, 200);
        Assert.IsNotNull(result.Latest);
        Assert.AreEqual(NumberDataPoint.ValueOneofCase.AsInt, result.Latest.ValueCase);
        Assert.AreEqual(9_007_199_254_740_993L, result.Latest.AsInt);
        Assert.AreEqual(2, result.Inputs);
        Assert.AreEqual(198UL, result.FirstObserved);
        Assert.AreEqual(199UL, result.LastObserved);
    }

    [TestMethod]
    public void MissingOrConflictingLatestSamplesDoNotReuseEarlierValue()
    {
        var missing = GaugeWindow.Reduce([
            new NumberDataPoint { TimeUnixNano = 101, AsInt = 42 },
            new NumberDataPoint { TimeUnixNano = 102, Flags = 1 }
        ], 100, 200);
        Assert.IsNull(missing.Latest);
        Assert.AreEqual(1, missing.Missing);

        var conflicting = GaugeWindow.Reduce([
            new NumberDataPoint { TimeUnixNano = 101, AsInt = 42 },
            new NumberDataPoint { TimeUnixNano = 102, AsInt = 43 },
            new NumberDataPoint { TimeUnixNano = 102, AsInt = 44 }
        ], 100, 200);
        Assert.IsNull(conflicting.Latest);
        Assert.IsTrue(conflicting.AmbiguousTimestamps);
    }

    [TestMethod]
    public void InvalidValuesAndWindowBoundaryRemainVisible()
    {
        var invalid = GaugeWindow.Reduce([new NumberDataPoint { TimeUnixNano = 100, AsDouble = double.NaN }], 100, 200);
        Assert.IsNull(invalid.Latest);
        Assert.AreEqual(1, invalid.Invalid);
        var empty = GaugeWindow.Reduce([], 100, 200);
        Assert.IsNull(empty.Latest);
        Assert.IsNull(empty.LastObserved);
        Assert.Throws<ArgumentException>(() => GaugeWindow.Reduce([new NumberDataPoint { TimeUnixNano = 200, AsInt = 1 }], 100, 200));
        Assert.Throws<ArgumentOutOfRangeException>(() => GaugeWindow.Reduce(Enumerable.Repeat(new NumberDataPoint(), GaugeWindow.MaximumPoints + 1).ToArray(), 100, 200));
    }

    [TestMethod]
    public void MissingFlagTakesPrecedenceAndEarlierAmbiguityRemainsVisible()
    {
        var missing = GaugeWindow.Reduce([new NumberDataPoint { TimeUnixNano = 101, Flags = 1, AsDouble = double.NaN }], 100, 200);
        Assert.AreEqual(1, missing.Missing);
        Assert.AreEqual(0, missing.Invalid);
        var unknownFlag = GaugeWindow.Reduce([new NumberDataPoint { TimeUnixNano = 101, Flags = 2, AsInt = 1 }], 100, 200);
        Assert.AreEqual(1, unknownFlag.Invalid);
        Assert.IsNull(unknownFlag.Latest);
        var infinite = GaugeWindow.Reduce([new NumberDataPoint { TimeUnixNano = 101, AsDouble = double.PositiveInfinity }], 100, 200);
        Assert.AreEqual(1, infinite.Invalid);
        var earlierConflict = GaugeWindow.Reduce([
            new NumberDataPoint { TimeUnixNano = 101, AsInt = 1 },
            new NumberDataPoint { TimeUnixNano = 101, AsInt = 2 },
            new NumberDataPoint { TimeUnixNano = 102, AsInt = 3 }
        ], 100, 200);
        Assert.IsTrue(earlierConflict.AmbiguousTimestamps);
        Assert.IsNotNull(earlierConflict.Latest);
        Assert.AreEqual(3L, earlierConflict.Latest.AsInt);
    }
}

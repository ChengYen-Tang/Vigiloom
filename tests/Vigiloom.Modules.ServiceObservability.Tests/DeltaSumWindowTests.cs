using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class DeltaSumWindowTests
{
    [TestMethod]
    public void ContiguousDeltasIncludeTheWindowEndAndPreserveIntegerPrecision()
    {
        var result = DeltaSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 200, TimeUnixNano = 300, AsInt = 2 },
            new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, AsInt = 9_007_199_254_740_993 }
        ], 100, 300, true);
        Assert.AreEqual(9_007_199_254_740_995L, result.Integer);
        Assert.IsNull(result.Floating);
        Assert.IsFalse(result.Gap);
        Assert.IsFalse(result.Overlap);
    }

    [TestMethod]
    public void MissingOverlappingAndCrossBoundaryIntervalsCannotProduceFullTotals()
    {
        var gap = DeltaSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 150, AsInt = 1 },
            new NumberDataPoint { StartTimeUnixNano = 200, TimeUnixNano = 300, AsInt = 2 }
        ], 100, 300, true);
        Assert.IsTrue(gap.Gap);
        Assert.IsNull(gap.Integer);
        var overlap = DeltaSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, AsInt = 1 },
            new NumberDataPoint { StartTimeUnixNano = 150, TimeUnixNano = 300, AsInt = 2 }
        ], 100, 300, true);
        Assert.IsTrue(overlap.Overlap);
        Assert.IsNull(overlap.Integer);
        var crossing = DeltaSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 300, AsInt = 10 }
        ], 100, 300, true);
        Assert.IsTrue(crossing.CrossesBoundary);
        Assert.IsNull(crossing.Integer);
        Assert.Throws<ArgumentException>(() => DeltaSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 100, AsInt = 1 }
        ], 100, 300, true));
    }

    [TestMethod]
    public void UnknownStartsMissingSamplesAndOverflowRemainUnknown()
    {
        var missing = DeltaSumWindow.Reduce([
            new NumberDataPoint { TimeUnixNano = 300, AsInt = 1 }
        ], 100, 300, true);
        Assert.AreEqual(1, missing.Missing);
        Assert.IsNull(missing.Integer);
        var overflow = DeltaSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, AsInt = long.MaxValue },
            new NumberDataPoint { StartTimeUnixNano = 200, TimeUnixNano = 300, AsInt = 1 }
        ], 100, 300, true);
        Assert.IsTrue(overflow.Overflow);
        Assert.IsNull(overflow.Integer);
        var negative = DeltaSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 300, AsInt = -1 }
        ], 100, 300, true);
        Assert.AreEqual(1, negative.Invalid);
        Assert.IsNull(negative.Integer);
    }

    [TestMethod]
    public void MixedNumericValuesRetainCancellationAndMissingSemantics()
    {
        var result = DeltaSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, AsInt = 9_007_199_254_740_992 },
            new NumberDataPoint { StartTimeUnixNano = 200, TimeUnixNano = 300, AsInt = 1 },
            new NumberDataPoint { StartTimeUnixNano = 300, TimeUnixNano = 400, AsInt = -9_007_199_254_740_992 },
            new NumberDataPoint { StartTimeUnixNano = 400, TimeUnixNano = 500, AsDouble = 0.5 }
        ], 100, 500, false);
        Assert.AreEqual(1.5, result.Floating);
        Assert.IsNull(result.Integer);
        Assert.IsFalse(result.Overflow);
        var floatingCancellation = DeltaSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, AsDouble = 10_000_000_000_000_000 },
            new NumberDataPoint { StartTimeUnixNano = 200, TimeUnixNano = 300, AsDouble = 1 },
            new NumberDataPoint { StartTimeUnixNano = 300, TimeUnixNano = 400, AsDouble = -10_000_000_000_000_000 }
        ], 100, 400, false);
        Assert.AreEqual(1.0, floatingCancellation.Floating);
        var missing = DeltaSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, Flags = 1, AsDouble = double.NaN }
        ], 100, 200, false);
        Assert.AreEqual(1, missing.Missing);
        Assert.AreEqual(0, missing.Invalid);
        var invalid = DeltaSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, AsDouble = double.PositiveInfinity }
        ], 100, 200, false);
        Assert.AreEqual(1, invalid.Invalid);
        Assert.IsNull(invalid.Floating);
    }
}

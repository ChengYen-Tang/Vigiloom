using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class CumulativeSumWindowTests
{
    [TestMethod]
    public void ResetEpochsAreRetainedWithoutAddingCumulativeSamples()
    {
        var result = CumulativeSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 110, AsInt = 10 },
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 120, AsInt = 15 },
            new NumberDataPoint { StartTimeUnixNano = 125, TimeUnixNano = 130, AsInt = 2 }
        ], 100, 200, true);
        Assert.HasCount(2, result.Epochs);
        var first = result.Epochs.Single(x => x.Start == 50);
        Assert.AreEqual(10L, first.First!.AsInt);
        Assert.AreEqual(15L, first.Last!.AsInt);
        Assert.AreEqual(2L, result.Epochs.Single(x => x.Start == 125).Last!.AsInt);
        Assert.IsFalse(result.OverlappingEpochs);
        Assert.IsFalse(result.Decreased);
    }

    [TestMethod]
    public void MissingAndConflictingLatestSnapshotsRemainUnknown()
    {
        var missing = CumulativeSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 110, AsInt = 10 },
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 120, Flags = 1, AsDouble = double.NaN }
        ], 100, 200, true);
        Assert.AreEqual(1, missing.Missing);
        Assert.AreEqual(0, missing.Invalid);
        Assert.IsNull(missing.Epochs.Single().Last);
        var conflict = CumulativeSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 110, AsInt = 10 },
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 110, AsInt = 20 }
        ], 100, 200, true);
        Assert.IsTrue(conflict.AmbiguousTimestamps);
        Assert.IsNull(conflict.Epochs.Single().First);
        Assert.IsNull(conflict.Epochs.Single().Last);
    }

    [TestMethod]
    public void UnknownResetsOverlapsAndDecreasesAreVisible()
    {
        var overlap = CumulativeSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 110, AsInt = 10 },
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 120, AsInt = 5 },
            new NumberDataPoint { StartTimeUnixNano = 115, TimeUnixNano = 130, AsInt = 1 }
        ], 100, 200, true);
        Assert.IsTrue(overlap.Decreased);
        Assert.IsTrue(overlap.OverlappingEpochs);
        var unknown = CumulativeSumWindow.Reduce([
            new NumberDataPoint { TimeUnixNano = 110, AsInt = 1 },
            new NumberDataPoint { TimeUnixNano = 120, AsDouble = 2 }
        ], 100, 200, true);
        Assert.IsTrue(unknown.UnknownStart);
        Assert.IsTrue(unknown.MonotonicityUnknown);
    }

    [TestMethod]
    public void MissingPointDoesNotHideDecreaseBetweenKnownSamples()
    {
        var result = CumulativeSumWindow.Reduce([
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 110, AsInt = 100 },
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 120, Flags = 1 },
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 130, AsInt = 90 }
        ], 100, 200, true);
        Assert.IsTrue(result.Decreased);
        Assert.AreEqual(1, result.Missing);
    }

    [TestMethod]
    public void ReturnedSnapshotsAreIndependentOfInputObjects()
    {
        var point = new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 110, AsInt = 10 };
        var result = CumulativeSumWindow.Reduce([point], 100, 200, true);
        point.AsInt = 99;
        Assert.AreEqual(10L, result.Epochs.Single().Last!.AsInt);
        Assert.AreEqual(10L, result.Epochs.Single().First!.AsInt);
    }
}

using Google.Protobuf;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class MetricProjectionTests
{
    private static readonly MetricWindow Window = new(100, 200);

    [TestMethod]
    public void CompleteDeltaSumUsesIntervalAggregate()
    {
        var metric = new Metric { Sum = new Sum { AggregationTemporality = AggregationTemporality.Delta, IsMonotonic = true } };
        IMessage[] points = [new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 150, AsInt = 3 },
            new NumberDataPoint { StartTimeUnixNano = 150, TimeUnixNano = 200, AsInt = 4 }];
        var result = Assert.IsInstanceOfType<MetricProjection.DeltaSum>(MetricProjection.Reduce(metric, points, Window));
        Assert.AreEqual(7L, result.Value.Integer);
        Assert.IsFalse(result.RequiresRaw);
    }

    [TestMethod]
    public void IncompleteDeltaAndOmittedExemplarsProtectInputs()
    {
        var metric = new Metric { Sum = new Sum { AggregationTemporality = AggregationTemporality.Delta } };
        IMessage[] gap = [new NumberDataPoint { StartTimeUnixNano = 120, TimeUnixNano = 200, AsInt = 4 }];
        Assert.IsTrue(MetricProjection.Reduce(metric, gap, Window).RequiresRaw);
        var point = new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, AsInt = 4 };
        point.Exemplars.Add(new Exemplar { TimeUnixNano = 150, AsInt = 4 });
        Assert.IsTrue(MetricProjection.Reduce(metric, [point], Window).RequiresRaw);
    }

    [TestMethod]
    public void UnsupportedTypesAndMismatchedPayloadsAlwaysProtectInputs()
    {
        var metric = new Metric { ExponentialHistogram = new ExponentialHistogram { AggregationTemporality = AggregationTemporality.Delta } };
        Assert.IsInstanceOfType<MetricProjection.Unsupported>(MetricProjection.Reduce(metric,
            [new ExponentialHistogramDataPoint { TimeUnixNano = 150 }], Window));
        var gauge = new Metric { Gauge = new Gauge() };
        Assert.IsTrue(MetricProjection.Reduce(gauge, [new HistogramDataPoint { TimeUnixNano = 150 }], Window).RequiresRaw);
    }

    [TestMethod]
    public void SnapshotIsExplicitAndMissingObservationsProtectInputs()
    {
        var metric = new Metric { Gauge = new Gauge() };
        IMessage[] points = [new NumberDataPoint { TimeUnixNano = 110, AsInt = 3 },
            new NumberDataPoint { TimeUnixNano = 190, AsInt = 4 }];
        var result = Assert.IsInstanceOfType<MetricProjection.Gauge>(MetricProjection.Reduce(metric, points, Window));
        Assert.AreEqual(4L, result.Value.Latest!.AsInt);
        Assert.IsFalse(result.RequiresRaw);
        points[1] = new NumberDataPoint { TimeUnixNano = 190, Flags = 1 };
        Assert.IsTrue(MetricProjection.Reduce(metric, points, Window).RequiresRaw);
    }

    [TestMethod]
    public void UnknownMetadataAndEmbeddedPointsCannotAuthorizeReduction()
    {
        var metadata = new Metric { Sum = new Sum { AggregationTemporality = AggregationTemporality.Delta } };
        byte[] extension = [0xa0, 0x06, 0x01];
        var unknown = Metric.Parser.ParseFrom(metadata.ToByteArray().Concat(extension).ToArray());
        IMessage[] points = [new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, AsInt = 1 }];
        Assert.IsTrue(MetricProjection.Reduce(unknown, points, Window).RequiresRaw);
        metadata.Sum.DataPoints.Add(new NumberDataPoint { TimeUnixNano = 150, AsInt = 1 });
        Assert.IsTrue(MetricProjection.Reduce(metadata, points, Window).RequiresRaw);
    }

    [TestMethod]
    public void CumulativeAndHistogramModesRetainTheirTemporalMeaning()
    {
        var cumulative = new Metric { Sum = new Sum { AggregationTemporality = AggregationTemporality.Cumulative, IsMonotonic = true } };
        IMessage[] counters = [new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 110, AsInt = 3 },
            new NumberDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 190, AsInt = 7 }];
        var snapshot = Assert.IsInstanceOfType<MetricProjection.CumulativeSum>(MetricProjection.Reduce(cumulative, counters, Window));
        Assert.AreEqual(3L, snapshot.Value.Epochs.Single().First!.AsInt);
        Assert.AreEqual(7L, snapshot.Value.Epochs.Single().Last!.AsInt);
        Assert.IsFalse(snapshot.RequiresRaw);

        var histogram = new Metric { Histogram = new Histogram { AggregationTemporality = AggregationTemporality.Delta } };
        IMessage[] points = [new HistogramDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, Count = 0, BucketCounts = { 0 } }];
        var aggregate = Assert.IsInstanceOfType<MetricProjection.DeltaHistogram>(MetricProjection.Reduce(histogram, points, Window));
        Assert.IsFalse(aggregate.RequiresRaw);
        Assert.AreEqual(0UL, aggregate.Value.Aggregate!.Count);
    }

    [TestMethod]
    public void SummaryWithUnknownQuantileWindowRemainsProtected()
    {
        var metric = new Metric { Summary = new Summary() };
        var point = new SummaryDataPoint { StartTimeUnixNano = 50, TimeUnixNano = 150, Count = 2, Sum = 10001 };
        point.QuantileValues.Add(new SummaryDataPoint.Types.ValueAtQuantile { Quantile = .5, Value = 1 });
        var result = Assert.IsInstanceOfType<MetricProjection.Summary>(MetricProjection.Reduce(metric, [point], Window));
        Assert.IsTrue(result.RequiresRaw);
        Assert.AreEqual(1d, result.Value.Epochs.Single().Last!.QuantileValues[0].Value);
    }
}

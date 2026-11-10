using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class MetricRollupEncodingTests
{
    private static readonly MetricWindow Window = new(MetricWindow.WidthNanoseconds, 2 * MetricWindow.WidthNanoseconds);

    [TestMethod]
    public void GaugeSnapshotPreservesOtlpContextAndLatestSample()
    {
        var metric = new Metric { Name = "load", Gauge = new Gauge() };
        var first = new NumberDataPoint { TimeUnixNano = Window.Start + 1, AsDouble = 2 };
        var last = new NumberDataPoint { TimeUnixNano = Window.Start + 2, AsDouble = 5 };
        var result = MetricRollupEncoding.Encode(Group(metric, 1, first, last), Window);
        Assert.IsNotNull(result);
        Assert.AreEqual(MetricRollupMode.GaugeSnapshot, result.Mode);
        Assert.IsFalse(result.RequiresRaw);
        var decoded = ResourceMetrics.Parser.ParseFrom(result.ResultPayload);
        Assert.AreEqual("schema-resource", decoded.SchemaUrl);
        Assert.AreEqual("test", decoded.Resource.Attributes.Single().Value.StringValue);
        Assert.AreEqual("schema-scope", decoded.ScopeMetrics.Single().SchemaUrl);
        Assert.AreEqual("meter", decoded.ScopeMetrics.Single().Scope.Name);
        var sample = decoded.ScopeMetrics.Single().Metrics.Single().Gauge.DataPoints.Single();
        Assert.AreEqual(Window.Start + 2, sample.TimeUnixNano);
        Assert.AreEqual(5d, sample.AsDouble);
    }

    [TestMethod]
    public void DeltaAggregateRetainsExactIntervalAndAttributes()
    {
        var metric = new Metric { Name = "requests", Sum = new Sum { AggregationTemporality = AggregationTemporality.Delta, IsMonotonic = true } };
        var a = new NumberDataPoint { StartTimeUnixNano = Window.Start, TimeUnixNano = Window.Start + MetricWindow.WidthNanoseconds / 2, AsInt = 7 };
        a.Attributes.Add(new KeyValue { Key = "route", Value = new AnyValue { StringValue = "orders" } });
        var b = a.Clone(); b.StartTimeUnixNano = a.TimeUnixNano; b.TimeUnixNano = Window.End; b.AsInt = 9;
        var result = MetricRollupEncoding.Encode(Group(metric, 2, a, b), Window);
        Assert.IsNotNull(result);
        Assert.AreEqual(MetricRollupMode.DeltaSum, result.Mode);
        Assert.IsFalse(result.RequiresRaw);
        var point = ResourceMetrics.Parser.ParseFrom(result.ResultPayload).ScopeMetrics.Single().Metrics.Single().Sum.DataPoints.Single();
        Assert.AreEqual(16L, point.AsInt);
        Assert.AreEqual(Window.Start, point.StartTimeUnixNano);
        Assert.AreEqual(Window.End, point.TimeUnixNano);
        Assert.AreEqual("orders", point.Attributes.Single().Value.StringValue);
    }

    [TestMethod]
    public void IncompleteAggregateDoesNotCreateACompleteResult()
    {
        var metric = new Metric { Name = "requests", Sum = new Sum { AggregationTemporality = AggregationTemporality.Delta } };
        var point = new NumberDataPoint { StartTimeUnixNano = Window.Start + 1, TimeUnixNano = Window.End, AsInt = 7 };
        var result = MetricRollupEncoding.Encode(Group(metric, 2, point), Window);
        Assert.IsNotNull(result);
        Assert.IsTrue(result.RequiresRaw);
        Assert.IsNull(result.ResultPayload);
        Assert.IsTrue(((MetricRollupQuality)result.QualityFlags).HasFlag(MetricRollupQuality.Gap));
        Assert.IsTrue(((MetricRollupQuality)result.QualityFlags).HasFlag(MetricRollupQuality.NoResult));
    }

    [TestMethod]
    public void UnknownPointFieldsAndExemplarsRemainProtected()
    {
        var metric = new Metric { Name = "requests", Sum = new Sum { AggregationTemporality = AggregationTemporality.Delta } };
        var point = new NumberDataPoint { StartTimeUnixNano = Window.Start, TimeUnixNano = Window.End, AsInt = 7 };
        var unknown = NumberDataPoint.Parser.ParseFrom(point.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 0x01 }).ToArray());
        var unsupported = MetricRollupEncoding.Encode(Group(metric, 2, unknown), Window);
        Assert.IsNotNull(unsupported);
        Assert.IsTrue(unsupported.RequiresRaw);
        Assert.IsNull(unsupported.ResultPayload);
        Assert.IsTrue(((MetricRollupQuality)unsupported.QualityFlags).HasFlag(MetricRollupQuality.Unsupported));
        point.Exemplars.Add(new Exemplar { TimeUnixNano = Window.Start + 1, AsInt = 7 });
        var exemplar = MetricRollupEncoding.Encode(Group(metric, 2, point), Window);
        Assert.IsNotNull(exemplar);
        Assert.IsTrue(exemplar.RequiresRaw);
        Assert.IsTrue(((MetricRollupQuality)exemplar.QualityFlags).HasFlag(MetricRollupQuality.ExemplarsRequireRaw));
    }

    [TestMethod]
    public void CumulativeResetsPreserveSnapshotsWithoutAddingThem()
    {
        var metric = new Metric { Name = "count", Sum = new Sum { AggregationTemporality = AggregationTemporality.Cumulative, IsMonotonic = true } };
        var a = new NumberDataPoint { StartTimeUnixNano = Window.Start - 10, TimeUnixNano = Window.Start + 1, AsInt = 100 };
        var b = new NumberDataPoint { StartTimeUnixNano = Window.Start - 10, TimeUnixNano = Window.Start + 2, AsInt = 105 };
        var c = new NumberDataPoint { StartTimeUnixNano = Window.Start + 3, TimeUnixNano = Window.Start + 4, AsInt = 2 };
        var result = MetricRollupEncoding.Encode(Group(metric, 2, a, b, c), Window);
        Assert.IsNotNull(result);
        Assert.AreEqual(MetricRollupMode.CumulativeSumSnapshots, result.Mode);
        var values = ResourceMetrics.Parser.ParseFrom(result.ResultPayload).ScopeMetrics.Single().Metrics.Single().Sum.DataPoints;
        CollectionAssert.AreEqual(new long[] { 100, 105, 2 }, values.Select(x => x.AsInt).ToArray());
        var single = MetricRollupEncoding.Encode(Group(metric, 2, a), Window);
        Assert.IsNotNull(single);
        Assert.HasCount(1, ResourceMetrics.Parser.ParseFrom(single.ResultPayload).ScopeMetrics.Single().Metrics.Single().Sum.DataPoints);
    }

    [TestMethod]
    public void HistogramAggregatePreservesCountsAndBounds()
    {
        var metric = new Metric { Name = "latency", Histogram = new Histogram { AggregationTemporality = AggregationTemporality.Delta } };
        var a = new HistogramDataPoint { StartTimeUnixNano = Window.Start, TimeUnixNano = Window.Start + MetricWindow.WidthNanoseconds / 2,
            Count = 2, Sum = 7, Min = 1, Max = 6, ExplicitBounds = { 5 }, BucketCounts = { 1, 1 } };
        var b = new HistogramDataPoint { StartTimeUnixNano = a.TimeUnixNano, TimeUnixNano = Window.End,
            Count = 1, Sum = 6, Min = 6, Max = 6, ExplicitBounds = { 5 }, BucketCounts = { 0, 1 } };
        var result = MetricRollupEncoding.Encode(Group(metric, 3, a, b), Window);
        Assert.IsNotNull(result);
        Assert.AreEqual(MetricRollupMode.DeltaHistogram, result.Mode);
        Assert.IsFalse(result.RequiresRaw);
        var aggregate = ResourceMetrics.Parser.ParseFrom(result.ResultPayload).ScopeMetrics.Single().Metrics.Single().Histogram.DataPoints.Single();
        Assert.AreEqual(3UL, aggregate.Count);
        Assert.AreEqual(13d, aggregate.Sum);
        Assert.AreEqual(Window.Start, aggregate.StartTimeUnixNano);
        Assert.AreEqual(Window.End, aggregate.TimeUnixNano);
        Assert.AreEqual(1d, aggregate.Min);
        Assert.AreEqual(6d, aggregate.Max);
        CollectionAssert.AreEqual(new double[] { 5 }, aggregate.ExplicitBounds.ToArray());
        CollectionAssert.AreEqual(new ulong[] { 1, 2 }, aggregate.BucketCounts.ToArray());
    }

    [TestMethod]
    public void SummaryQuantileSnapshotIsPreservedButRawRemainsProtected()
    {
        var metric = new Metric { Name = "latency", Summary = new Summary() };
        var point = new SummaryDataPoint { StartTimeUnixNano = Window.Start - 10, TimeUnixNano = Window.Start + 1, Count = 2, Sum = 10001 };
        point.QuantileValues.Add(new SummaryDataPoint.Types.ValueAtQuantile { Quantile = .5, Value = 1 });
        var result = MetricRollupEncoding.Encode(Group(metric, 5, point), Window);
        Assert.IsNotNull(result);
        Assert.IsTrue(result.RequiresRaw);
        Assert.IsTrue(((MetricRollupQuality)result.QualityFlags).HasFlag(MetricRollupQuality.StatisticsUnverified));
        var snapshot = ResourceMetrics.Parser.ParseFrom(result.ResultPayload).ScopeMetrics.Single().Metrics.Single().Summary.DataPoints.Single();
        Assert.AreEqual(10001d, snapshot.Sum);
        Assert.AreEqual(1d, snapshot.QuantileValues.Single().Value);
    }

    private static MetricWindowGrouping.Series Group(Metric metric, byte kind, params IMessage[] points)
    {
        var source = Guid.CreateVersion7();
        var service = Guid.CreateVersion7();
        var resource = new Resource { Attributes = { new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "test" } } } };
        var context = new MetricContext { ServiceId = service, ResourcePayload = resource.ToByteArray(),
            ScopePayload = new InstrumentationScope { Name = "meter" }.ToByteArray(), MetricPayload = metric.ToByteArray(),
            ResourceSchemaUrl = "schema-resource", ScopeSchemaUrl = "schema-scope" };
        var rows = points.Select(point => new MetricPoint { ServiceId = service, Kind = kind,
            PointPayload = point.ToByteArray(), Context = context }).ToArray();
        var result = MetricWindowGrouping.Group(source, service, Window, rows);
        Assert.IsFalse(result.RequiresRaw);
        return result.Series.Single();
    }
}

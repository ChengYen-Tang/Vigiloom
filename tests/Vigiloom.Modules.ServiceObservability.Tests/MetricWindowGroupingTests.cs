using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class MetricWindowGroupingTests
{
    [TestMethod]
    public void GroupsCanonicalAttributesWithoutDiscardingDuplicatePoints()
    {
        var source = Guid.CreateVersion7();
        var service = Guid.CreateVersion7();
        var instance = Guid.CreateVersion7();
        var window = new MetricWindow(MetricWindow.WidthNanoseconds, 2 * MetricWindow.WidthNanoseconds);
        var metric = new Metric { Name = "load", Gauge = new Gauge() };
        var first = new NumberDataPoint { TimeUnixNano = window.Start + 10, AsInt = 3 };
        first.Attributes.Add(Attribute("a", "1"));
        first.Attributes.Add(Attribute("b", "2"));
        var reordered = first.Clone();
        reordered.Attributes.Clear();
        reordered.Attributes.Add(Attribute("b", "2"));
        reordered.Attributes.Add(Attribute("a", "1"));
        var rows = new[] { Row(service, instance, metric, first), Row(service, instance, metric, reordered),
            Row(service, Guid.CreateVersion7(), metric, first) };
        var result = MetricWindowGrouping.Group(source, service, window, rows);
        Assert.IsFalse(result.RequiresRaw);
        Assert.HasCount(2, result.Series);
        Assert.AreEqual(2, result.Series.Max(x => x.Points.Count));
        Assert.AreEqual(3, result.Series.Sum(x => x.Points.Count));
    }

    [TestMethod]
    public void DistinctSimilarAttributeValuesRemainDistinctSeries()
    {
        var source = Guid.CreateVersion7();
        var service = Guid.CreateVersion7();
        var window = new MetricWindow(MetricWindow.WidthNanoseconds, 2 * MetricWindow.WidthNanoseconds);
        var metric = new Metric { Name = "load", Gauge = new Gauge() };
        var rows = new List<MetricPoint>();
        for (var value = 0; value < 256; value++)
        {
            var label = string.Concat(Enumerable.Range(0, 8).Select(bit => (value & 1 << bit) == 0 ? "Aa" : "BB"));
            var point = new NumberDataPoint { TimeUnixNano = window.Start + 10, AsInt = 3 };
            point.Attributes.Add(Attribute("label", label));
            rows.Add(Row(service, null, metric, point));
        }
        var result = MetricWindowGrouping.Group(source, service, window, rows);
        Assert.IsFalse(result.RequiresRaw);
        Assert.HasCount(256, result.Series);
    }

    [TestMethod]
    public void ClassifiesBoundaryByMetricTemporalityUsingNanoseconds()
    {
        var source = Guid.CreateVersion7();
        var service = Guid.CreateVersion7();
        var window = new MetricWindow(MetricWindow.WidthNanoseconds, 2 * MetricWindow.WidthNanoseconds);
        var gauge = new Metric { Name = "gauge", Gauge = new Gauge() };
        var delta = new Metric { Name = "delta", Sum = new Sum { AggregationTemporality = AggregationTemporality.Delta } };
        var rows = new[] {
            Row(service, null, gauge, new NumberDataPoint { TimeUnixNano = window.Start, AsInt = 1 }),
            Row(service, null, gauge, new NumberDataPoint { TimeUnixNano = window.End, AsInt = 2 }),
            Row(service, null, delta, new NumberDataPoint { StartTimeUnixNano = window.Start - 1, TimeUnixNano = window.Start, AsInt = 3 }),
            Row(service, null, delta, new NumberDataPoint { StartTimeUnixNano = window.Start, TimeUnixNano = window.Start + 1, AsInt = 4 }) };
        var result = MetricWindowGrouping.Group(source, service, window, rows);
        Assert.IsFalse(result.RequiresRaw);
        Assert.HasCount(2, result.Series);
        Assert.AreEqual(2, result.Series.Sum(x => x.Points.Count));
        Assert.AreEqual(1L, ((NumberDataPoint)result.Series.Single(x => x.Metadata.Name == "gauge").Points.Single()).AsInt);
        Assert.AreEqual(4L, ((NumberDataPoint)result.Series.Single(x => x.Metadata.Name == "delta").Points.Single()).AsInt);
    }

    [TestMethod]
    public void InvalidOrUnidentifiableCandidateRequiresRawProtection()
    {
        var source = Guid.CreateVersion7();
        var service = Guid.CreateVersion7();
        var window = new MetricWindow(MetricWindow.WidthNanoseconds, 2 * MetricWindow.WidthNanoseconds);
        var row = Row(service, null, new Metric { Name = "gauge", Gauge = new Gauge() },
            new NumberDataPoint { TimeUnixNano = window.Start + 1, AsInt = 1 });
        row.PointPayload = [0xff];
        var invalid = MetricWindowGrouping.Group(source, service, window, [row]);
        Assert.IsTrue(invalid.RequiresRaw);
        Assert.IsEmpty(invalid.Series);
        row = Row(service, null, new Metric { Gauge = new Gauge() }, new NumberDataPoint { TimeUnixNano = window.Start + 1, AsInt = 1 });
        Assert.IsTrue(MetricWindowGrouping.Group(source, service, window, [row]).RequiresRaw);
        row = Row(service, null, new Metric { Name = "gauge", Gauge = new Gauge() },
            new NumberDataPoint { TimeUnixNano = window.Start + 1, AsInt = 1 });
        row.Context.ScopePayload = [0xff];
        var repeatedInvalidContext = MetricWindowGrouping.Group(source, service, window, [row, row]);
        Assert.IsTrue(repeatedInvalidContext.RequiresRaw);
        Assert.IsEmpty(repeatedInvalidContext.Series);
    }

    [TestMethod]
    public void UnknownPointExtensionRemainsProtectedAfterGroupingAndProjection()
    {
        var source = Guid.CreateVersion7();
        var service = Guid.CreateVersion7();
        var window = new MetricWindow(MetricWindow.WidthNanoseconds, 2 * MetricWindow.WidthNanoseconds);
        var metric = new Metric { Name = "gauge", Gauge = new Gauge() };
        var bytes = new NumberDataPoint { TimeUnixNano = window.Start + 1, AsInt = 1 }.ToByteArray();
        var extended = NumberDataPoint.Parser.ParseFrom(bytes.Concat(new byte[] { 0xa0, 0x06, 0x01 }).ToArray());
        var groups = MetricWindowGrouping.Group(source, service, window, [Row(service, null, metric, extended)]);
        Assert.IsFalse(groups.RequiresRaw);
        var series = groups.Series.Single();
        Assert.IsTrue(MetricProjection.Reduce(series.Metadata, series.Points, window).RequiresRaw);
    }

    private static KeyValue Attribute(string key, string value) => new() { Key = key, Value = new AnyValue { StringValue = value } };

    private static MetricPoint Row(Guid service, Guid? instance, Metric metadata, NumberDataPoint point)
    {
        var resource = new Resource { Attributes = { Attribute("service.name", "test") } };
        return new MetricPoint { ServiceId = service, InstanceId = instance, Kind = metadata.DataCase == Metric.DataOneofCase.Gauge ? (byte)1 : (byte)2,
            PointPayload = point.ToByteArray(), Context = new MetricContext { ServiceId = service, ResourcePayload = resource.ToByteArray(),
                ScopePayload = [], MetricPayload = metadata.ToByteArray(), ResourceSchemaUrl = "", ScopeSchemaUrl = "" } };
    }
}

using OpenTelemetry.Proto.Common.V1;
using Google.Protobuf;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class MetricSeriesIdentityTests
{
    [TestMethod]
    public void AttributeOrderDoesNotChangeSeriesButValuesAndCaseDo()
    {
        var source = Guid.CreateVersion7();
        var service = Guid.CreateVersion7();
        var instance = Guid.CreateVersion7();
        var resource = new Resource { Attributes = { Attr("host", "one"), Attr("region", "north") } };
        var scope = new InstrumentationScope { Name = "example", Attributes = { Attr("x", "1"), Attr("y", "2") } };
        var metric = new Metric { Name = "requests", Unit = "1", Sum = new Sum { AggregationTemporality = AggregationTemporality.Cumulative, IsMonotonic = true } };
        var nested = new AnyValue { KvlistValue = new KeyValueList { Values = { Attr("b", "2"), Attr("a", "1") } } };
        var first = new[] { new KeyValue { Key = "nested", Value = nested }, Attr("method", "GET") };
        var reorderedResource = new Resource { Attributes = { Attr("region", "north"), Attr("host", "one") } };
        var reorderedScope = new InstrumentationScope { Name = "example", Attributes = { Attr("y", "2"), Attr("x", "1") } };
        var reordered = new[] { Attr("method", "GET"), new KeyValue { Key = "nested", Value = new AnyValue
            { KvlistValue = new KeyValueList { Values = { Attr("a", "1"), Attr("b", "2") } } } } };
        var expected = Create(source, service, instance, resource, scope, metric, first);
        var equivalent = Create(source, service, instance, reorderedResource, reorderedScope, metric, reordered);
        Assert.IsTrue(expected.SameSeries(equivalent));
        Assert.AreEqual(expected.Fingerprint, equivalent.Fingerprint);

        reordered[0].Value.StringValue = "get";
        Assert.IsFalse(expected.SameSeries(Create(source, service, instance, reorderedResource, reorderedScope, metric, reordered)));
        Assert.IsTrue(expected.SameSeries(equivalent), "Mutating input after construction must not change an existing identity.");
    }

    [TestMethod]
    public void SourcesUnitsAndSumSemanticsAreSeparate()
    {
        var source = Guid.CreateVersion7();
        var service = Guid.CreateVersion7();
        var resource = new Resource();
        var metric = new Metric { Name = "requests", Unit = "1", Sum = new Sum { AggregationTemporality = AggregationTemporality.Cumulative, IsMonotonic = true } };
        var original = Create(source, service, null, resource, null, metric, []);
        Assert.IsFalse(original.SameSeries(Create(Guid.CreateVersion7(), service, null, resource, null, metric, [])));
        var differentUnit = metric.Clone(); differentUnit.Unit = "ms";
        Assert.IsFalse(original.SameSeries(Create(source, service, null, resource, null, differentUnit, [])));
        var differentTemporality = metric.Clone(); differentTemporality.Sum.AggregationTemporality = AggregationTemporality.Delta;
        Assert.IsFalse(original.SameSeries(Create(source, service, null, resource, null, differentTemporality, [])));
        var differentMonotonicity = metric.Clone(); differentMonotonicity.Sum.IsMonotonic = false;
        Assert.IsFalse(original.SameSeries(Create(source, service, null, resource, null, differentMonotonicity, [])));
        var differentDescription = metric.Clone();
        differentDescription.Description = "new description";
        differentDescription.Metadata.Add(Attr("hint", "changed"));
        Assert.IsTrue(original.SameSeries(Create(source, service, null, resource, new InstrumentationScope(), differentDescription, [])));
        var unknownExtension = Metric.Parser.ParseFrom(metric.ToByteArray().Concat(new byte[] { 0xa0, 0x06, 0x01 }).ToArray());
        Assert.IsFalse(MetricSeriesIdentity.TryCreate(source, service, null, resource, null, "", "", unknownExtension, [], out _),
            "Unknown protocol fields must not be silently merged into a known series.");
    }

    [TestMethod]
    public void AmbiguousAttributesAndIncompleteResourceAreRejected()
    {
        var source = Guid.CreateVersion7();
        var service = Guid.CreateVersion7();
        var metric = new Metric { Name = "load", Gauge = new Gauge() };
        Assert.IsFalse(MetricSeriesIdentity.TryCreate(source, service, null, new Resource(), null, "", "", metric,
            [Attr("x", "1"), Attr("x", "2")], out _));
        Assert.IsFalse(MetricSeriesIdentity.TryCreate(source, service, null, new Resource(), null, "", "", metric,
            [null!], out _));
        Assert.IsFalse(MetricSeriesIdentity.TryCreate(source, service, null, new Resource(), null, "", "", metric,
            Enumerable.Range(0, 4097).Select(i => Attr($"key-{i}", "value")), out _));
        Assert.IsFalse(MetricSeriesIdentity.TryCreate(source, service, null, new Resource(), null, "", "", metric,
            [Attr("large", new string('x', 262_145))], out _));
        var deep = new AnyValue { StringValue = "value" };
        for (var i = 0; i < 40; i++) deep = new AnyValue { ArrayValue = new ArrayValue { Values = { deep } } };
        Assert.IsFalse(MetricSeriesIdentity.TryCreate(source, service, null, new Resource(), null, "", "", metric,
            [new KeyValue { Key = "deep", Value = deep }], out _));
        Assert.IsFalse(MetricSeriesIdentity.TryCreate(source, service, null, new Resource { DroppedAttributesCount = 1 }, null, "", "", metric, [], out _));
        var withPoints = metric.Clone(); withPoints.Gauge.DataPoints.Add(new NumberDataPoint { TimeUnixNano = 1 });
        Assert.IsFalse(MetricSeriesIdentity.TryCreate(source, service, null, new Resource(), null, "", "", withPoints, [], out _));
        var unspecified = new Metric { Name = "requests", Sum = new Sum() };
        Assert.IsFalse(MetricSeriesIdentity.TryCreate(source, service, null, new Resource(), null, "", "", unspecified, [], out _));
    }

    private static KeyValue Attr(string key, string value) => new() { Key = key, Value = new AnyValue { StringValue = value } };

    private static MetricSeriesIdentity Create(Guid source, Guid service, Guid? instance, Resource resource,
        InstrumentationScope? scope, Metric metric, IEnumerable<KeyValue> attributes)
    {
        Assert.IsTrue(MetricSeriesIdentity.TryCreate(source, service, instance, resource, scope, "", "", metric, attributes, out var result));
        Assert.IsNotNull(result);
        return result;
    }
}

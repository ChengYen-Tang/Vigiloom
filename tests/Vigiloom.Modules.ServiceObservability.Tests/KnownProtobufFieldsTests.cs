using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class KnownProtobufFieldsTests
{
    [TestMethod]
    public void KnownPointsAndPackedHistogramBucketsAreAccepted()
    {
        var point = new NumberDataPoint { TimeUnixNano = 100, AsInt = 4,
            Attributes = { new KeyValue { Key = "service", Value = new AnyValue { StringValue = "one" } } } };
        var budget = KnownProtobufFields.MaximumWindowBytes;
        Assert.AreEqual(KnownProtobufFields.Check.Known, KnownProtobufFields.Inspect(point, ref budget));
        var histogram = new HistogramDataPoint { TimeUnixNano = 100, Count = 3, BucketCounts = { 1, 2 }, ExplicitBounds = { 5 } };
        Assert.AreEqual(KnownProtobufFields.Check.Known, KnownProtobufFields.Inspect(histogram, ref budget));
    }

    [TestMethod]
    public void UnknownTopLevelAndNestedFieldsAreNotSilentlyDropped()
    {
        byte[] extension = [0xa0, 0x06, 0x01];
        var point = NumberDataPoint.Parser.ParseFrom(new NumberDataPoint { TimeUnixNano = 100, AsInt = 4 }.ToByteArray().Concat(extension).ToArray());
        var budget = KnownProtobufFields.MaximumWindowBytes;
        Assert.AreEqual(KnownProtobufFields.Check.Unsupported, KnownProtobufFields.Inspect(point, ref budget));
        var attribute = KeyValue.Parser.ParseFrom(new KeyValue { Key = "x", Value = new AnyValue { StringValue = "y" } }
            .ToByteArray().Concat(extension).ToArray());
        var nested = new NumberDataPoint { TimeUnixNano = 100, AsInt = 4, Attributes = { attribute } };
        Assert.AreEqual(KnownProtobufFields.Check.Unsupported, KnownProtobufFields.Inspect(nested, ref budget));
    }

    [TestMethod]
    public void CapacityLimitDoesNotExamineOrAcceptOversizedData()
    {
        var budget = 1L;
        var point = new NumberDataPoint { TimeUnixNano = 100, AsInt = 4 };
        Assert.AreEqual(KnownProtobufFields.Check.Capacity, KnownProtobufFields.Inspect(point, ref budget));
        Assert.AreEqual(1L, budget);
        var value = new AnyValue { StringValue = "leaf" };
        for (var i = 0; i < 40; i++) value = new AnyValue { ArrayValue = new ArrayValue { Values = { value } } };
        var deep = new NumberDataPoint { TimeUnixNano = 100, AsInt = 4, Attributes = { new KeyValue { Key = "deep", Value = value } } };
        budget = KnownProtobufFields.MaximumWindowBytes;
        Assert.AreEqual(KnownProtobufFields.Check.Capacity, KnownProtobufFields.Inspect(deep, ref budget));
    }

    [TestMethod]
    public void WireValidationAcceptsValidUnpackedAndRejectsWrongOrOversizedVarints()
    {
        byte[] unpackedFixed64 = [0x31, 1, 0, 0, 0, 0, 0, 0, 0];
        Assert.AreEqual(KnownProtobufFields.Check.Known, KnownProtobufFields.InspectBytes(unpackedFixed64, HistogramDataPoint.Descriptor));
        Assert.AreEqual(KnownProtobufFields.Check.Unsupported, KnownProtobufFields.InspectBytes([0x18, 0x01], NumberDataPoint.Descriptor));
        byte[] maximumUInt64 = [0x10, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01];
        Assert.AreEqual(KnownProtobufFields.Check.Known, KnownProtobufFields.InspectBytes(maximumUInt64, ExponentialHistogramDataPoint.Types.Buckets.Descriptor));
        maximumUInt64[^1] = 0x02;
        Assert.AreEqual(KnownProtobufFields.Check.Unsupported, KnownProtobufFields.InspectBytes(maximumUInt64, ExponentialHistogramDataPoint.Types.Buckets.Descriptor));
    }

    [TestMethod]
    public void PackedValuesConsumeTheFieldBudget()
    {
        var wire = new byte[100_004];
        wire[0] = 0x12; wire[1] = 0xa0; wire[2] = 0x8d; wire[3] = 0x06;
        Array.Fill(wire, (byte)1, 4, 100_000);
        Assert.AreEqual(KnownProtobufFields.Check.Capacity, KnownProtobufFields.InspectBytes(wire, ExponentialHistogramDataPoint.Types.Buckets.Descriptor));
    }
}

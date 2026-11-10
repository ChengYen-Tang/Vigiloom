using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class RollupSafetyTests
{
    [TestMethod]
    public void UnknownPointFieldsNeverProduceAnApparentlyCompleteValue()
    {
        byte[] extension = [0xa0, 0x06, 0x01];
        var scalar = NumberDataPoint.Parser.ParseFrom(new NumberDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, AsInt = 1 }
            .ToByteArray().Concat(extension).ToArray());
        var gauge = GaugeWindow.Reduce([scalar], 100, 300);
        Assert.IsTrue(gauge.UnsupportedFields);
        Assert.IsNull(gauge.Latest);
        var cumulative = CumulativeSumWindow.Reduce([scalar], 100, 300, true);
        Assert.IsTrue(cumulative.UnsupportedFields);
        var delta = DeltaSumWindow.Reduce([scalar], 100, 200, true);
        Assert.IsTrue(delta.UnsupportedFields);
        Assert.IsNull(delta.Integer);
        var histogram = HistogramDataPoint.Parser.ParseFrom(new HistogramDataPoint { StartTimeUnixNano = 100, TimeUnixNano = 200, Count = 1 }
            .ToByteArray().Concat(extension).ToArray());
        var distribution = DeltaHistogramWindow.Reduce([histogram], 100, 200);
        Assert.IsTrue(distribution.UnsupportedFields);
        Assert.IsNull(distribution.Aggregate);
    }

    [TestMethod]
    public void OversizedPointPreventsReductionInsteadOfDroppingFields()
    {
        var point = new NumberDataPoint { TimeUnixNano = 200, AsInt = 1, Attributes =
            { new KeyValue { Key = "large", Value = new AnyValue { StringValue = new string('x', 262_145) } } } };
        var result = GaugeWindow.Reduce([point], 100, 300);
        Assert.IsTrue(result.CapacityExceeded);
        Assert.IsNull(result.Latest);
    }
}

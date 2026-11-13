using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class LogBodyFormatterTests
{
    [TestMethod]
    public void FormatsStructuredValuesWithoutDiscardingTheirMeaning()
    {
        var body = new AnyValue { KvlistValue = new KeyValueList { Values = {
            new KeyValue { Key = "status", Value = new AnyValue { IntValue = 503 } },
            new KeyValue { Key = "retriable", Value = new AnyValue { BoolValue = true } },
            new KeyValue { Key = "tags", Value = new AnyValue { ArrayValue = new ArrayValue { Values = { new AnyValue { StringValue = "edge" } } } } },
            new KeyValue { Key = "data", Value = new AnyValue { BytesValue = ByteString.CopyFrom([1, 2]) } }
        } } };
        var result = LogBodyFormatter.Format(body);
        Assert.Contains("\"status\": 503", result.Text);
        Assert.Contains("\"retriable\": true", result.Text);
        Assert.Contains("[\"edge\"]", result.Text);
        Assert.Contains("base64:AQI=", result.Text);
        Assert.IsFalse(result.Truncated);
    }

    [TestMethod]
    public void StructuredStringsCannotMasqueradeAsOtherValuesOrFields()
    {
        var values = new ArrayValue { Values = { new AnyValue { StringValue = "false, admin: true" }, new AnyValue { BoolValue = false } } };
        var result = LogBodyFormatter.Format(new AnyValue { ArrayValue = values });
        Assert.AreEqual("[\"false, admin: true\", false]", result.Text);
        var escaped = new AnyValue { KvlistValue = new KeyValueList { Values = {
            new KeyValue { Key = "a\"b", Value = new AnyValue { StringValue = "x\\y\nz" } }
        } } };
        Assert.AreEqual("{\"a\\\"b\": \"x\\\\y\\nz\"}", LogBodyFormatter.Format(escaped).Text);
    }

    [TestMethod]
    public void BoundsLongTextAndNestedCollections()
    {
        var longText = LogBodyFormatter.Format(new AnyValue { StringValue = new string('x', 5000) });
        Assert.AreEqual(2000, longText.Text.Length);
        Assert.IsTrue(longText.Truncated);
        var emoji = LogBodyFormatter.Format(new AnyValue { StringValue = new string('x', 1999) + "😀" });
        Assert.AreEqual(1999, emoji.Text.Length);
        Assert.IsTrue(emoji.Truncated);
        var values = new ArrayValue();
        values.Values.AddRange(Enumerable.Range(0, 100).Select(x => new AnyValue { IntValue = x }));
        var many = LogBodyFormatter.Format(new AnyValue { ArrayValue = values });
        Assert.IsTrue(many.Truncated);
        Assert.IsLessThanOrEqualTo(2000, many.Text.Length);
    }
}

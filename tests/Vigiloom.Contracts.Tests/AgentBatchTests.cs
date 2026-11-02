using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class AgentBatchTests
{
    [TestMethod]
    public void Codec_RoundTripsStableIdsAndOrderedMessages()
    {
        var now = DateTimeOffset.UtcNow;
        var first = Runtime(now);
        var second = Runtime(now.AddSeconds(1));
        var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [first, second]);
        var payload = AgentBatchCodec.Serialize(batch);
        Assert.IsTrue(AgentBatchCodec.TryRead(payload, out var decoded));
        Assert.IsNotNull(decoded);
        Assert.AreEqual(batch.BatchId, decoded.BatchId);
        Assert.AreEqual(first, decoded.Items[0]);
        Assert.AreEqual(second, decoded.Items[1]);
        CollectionAssert.AreEqual(payload, AgentBatchCodec.Serialize(decoded));
    }

    [TestMethod]
    public void Codec_RejectsUnknownFieldsVersionsKindsAndDuplicateMessageIds()
    {
        var now = DateTimeOffset.UtcNow;
        var item = Runtime(now);
        var valid = new AgentBatch(1, Guid.CreateVersion7(), now, [item]);
        var payload = AgentBatchCodec.Serialize(valid);
        var json = JsonNode.Parse(payload)!.AsObject();
        json["extra"] = "not understood";
        Assert.IsFalse(AgentBatchCodec.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _));
        json.Remove("extra");
        json["schemaVersion"] = 2;
        Assert.IsFalse(AgentBatchCodec.TryRead(Encoding.UTF8.GetBytes(json.ToJsonString()), out _));
        Assert.Throws<ArgumentException>(() => AgentBatchCodec.Serialize(valid with { Items = [item, item] }));
        Assert.Throws<ArgumentException>(() => AgentBatchCodec.Serialize(valid with { Items = [item with { Kind = "unknown" }] }));
        var repeatedProperty = Encoding.UTF8.GetString(payload).Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
        Assert.IsFalse(AgentBatchCodec.TryRead(Encoding.UTF8.GetBytes(repeatedProperty), out _));
        var repeatedItemProperty = Encoding.UTF8.GetString(payload).Replace("\"kind\":\"agent.runtime.v1\"", "\"kind\":\"agent.runtime.v1\",\"kind\":\"agent.runtime.v1\"", StringComparison.Ordinal);
        Assert.AreNotEqual(Encoding.UTF8.GetString(payload), repeatedItemProperty);
        Assert.IsFalse(AgentBatchCodec.TryRead(Encoding.UTF8.GetBytes(repeatedItemProperty), out _));
    }

    [TestMethod]
    public void Codec_RejectsOversizedInvalidAndNonUtcContent()
    {
        var now = DateTimeOffset.UtcNow;
        var item = Runtime(now);
        var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [item]);
        Assert.IsFalse(AgentBatchCodec.TryRead(new byte[AgentBatchCodec.MaximumBytes + 1], out _));
        Assert.IsFalse(AgentBatchCodec.TryRead("{"u8, out _));
        Assert.Throws<ArgumentException>(() => AgentBatchCodec.Serialize(batch with { EnqueuedAt = now.ToOffset(TimeSpan.FromHours(1)) }));
        Assert.Throws<ArgumentException>(() => AgentBatchCodec.Serialize(batch with { Items = [item with { OperatingSystem = new string('x', 257) }] }));
        Assert.Throws<ArgumentException>(() => AgentBatchCodec.Serialize(batch with { Items = [item with { RuntimeVersion = "x\ny" }] }));
        Assert.Throws<ArgumentException>(() => AgentBatchCodec.Serialize(batch with { Items = [] }));
        var many = batch with { Items = Enumerable.Range(0, AgentBatchCodec.MaximumItems + 1).Select(_ => Runtime(now)).ToArray() };
        var raw = JsonSerializer.SerializeToUtf8Bytes(many, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsFalse(AgentBatchCodec.TryRead(raw, out _));
    }

    private static AgentRuntimeMessage Runtime(DateTimeOffset at) =>
        new(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, at, "Example OS", "X64", ".NET 10");
}

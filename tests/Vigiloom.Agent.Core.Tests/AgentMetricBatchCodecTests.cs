using System.Text;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentMetricBatchCodecTests
{
    [TestMethod]
    public void Serialize_RoundTripsWithoutChangingLegacyBatchFormat()
    {
        var now = DateTimeOffset.UtcNow;
        var point = new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.NetworkReceivedBytesPerSecond, "linux.net", "eth0", 0);
        var batch = new AgentMetricBatch(2, Guid.CreateVersion7(), now, [point]);
        var bytes = AgentMetricBatchCodec.Serialize(batch);
        Assert.IsTrue(AgentMetricBatchCodec.TryRead(bytes, out var decoded));
        Assert.IsNotNull(decoded);
        Assert.AreEqual(batch.BatchId, decoded.BatchId);
        Assert.AreEqual(point, decoded.Items[0]);
        Assert.IsFalse(AgentBatchCodec.TryRead(bytes, out _));
        var legacy = new AgentBatch(1, Guid.CreateVersion7(), now,
            [new(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "os", "x64", "runtime")]);
        var legacyBytes = AgentBatchCodec.Serialize(legacy);
        Assert.IsTrue(AgentBatchCodec.TryRead(legacyBytes, out _));
        Assert.IsFalse(AgentMetricBatchCodec.TryRead(legacyBytes, out _));
    }

    [TestMethod]
    public void Serialize_RejectsInvalidValuesDimensionsAndDuplicateIds()
    {
        var now = DateTimeOffset.UtcNow;
        var point = new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.CpuCorePercent, "linux.proc", "0", 50);
        var batch = new AgentMetricBatch(2, Guid.CreateVersion7(), now, [point]);
        Assert.Throws<ArgumentException>(() => AgentMetricBatchCodec.Serialize(batch with { Items = [point, point] }));
        Assert.Throws<ArgumentException>(() => AgentMetricBatchCodec.Serialize(batch with { Items = [point with { Value = double.NaN }] }));
        Assert.Throws<ArgumentException>(() => AgentMetricBatchCodec.Serialize(batch with { Items = [point with { Value = 101 }] }));
        Assert.Throws<ArgumentException>(() => AgentMetricBatchCodec.Serialize(batch with { Items = [point with { Dimension = null }] }));
        Assert.Throws<ArgumentException>(() => AgentMetricBatchCodec.Serialize(batch with { Items = [point with { Kind = (AgentMetricKind)999 }] }));
        Assert.Throws<ArgumentException>(() => AgentMetricBatchCodec.Serialize(batch with { Items = [point with { Kind = AgentMetricKind.CollectionUnavailable, Value = 0 }] }));
        Assert.Throws<ArgumentException>(() => AgentMetricBatchCodec.Serialize(batch with { Items = [point with { Kind = AgentMetricKind.CollectionUnavailable, Dimension = null, Value = 1 }] }));
        Assert.Throws<ArgumentException>(() => AgentMetricBatchCodec.Serialize(batch with { Items = [point with { Kind = AgentMetricKind.MemoryTotalBytes, Dimension = null, Value = 1e308 }] }));
    }

    [TestMethod]
    public void TryRead_RejectsMissingValueAndUnknownFields()
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var id = Guid.CreateVersion7();
        var point = Guid.CreateVersion7();
        var json = $$"""{"schemaVersion":2,"batchId":"{{id}}","enqueuedAt":"{{now}}","items":[{"messageId":"{{point}}","occurredAt":"{{now}}","kind":10,"source":"test"}]}""";
        Assert.IsFalse(AgentMetricBatchCodec.TryRead(Encoding.UTF8.GetBytes(json), out _));
        var unknown = json.Replace("\"source\":\"test\"", "\"source\":\"test\",\"value\":1,\"unexpected\":true", StringComparison.Ordinal);
        Assert.IsFalse(AgentMetricBatchCodec.TryRead(Encoding.UTF8.GetBytes(unknown), out _));
        var extreme = json.Replace("\"source\":\"test\"", "\"source\":\"test\",\"value\":1e308", StringComparison.Ordinal);
        Assert.IsFalse(AgentMetricBatchCodec.TryRead(Encoding.UTF8.GetBytes(extreme), out _));
    }
}

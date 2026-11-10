using System.Text;
using System.Text.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class AgentLogBatchTests
{
    [TestMethod]
    public void Codec_PreservesKnownAndUnknownGapCountsAndRejectsAmbiguousRecords()
    {
        var batch = CreateBatch();
        var gap = new AgentLogRecord(Guid.CreateVersion7(), DateTimeOffset.UtcNow, "", null, null, null, null, null,
            new AgentLogGap(AgentLogGapReason.CursorLost, null));
        var data = batch with { Items = [gap, gap with { MessageId = Guid.CreateVersion7(), Gap = new(AgentLogGapReason.Oversized, 1) }] };
        Assert.IsTrue(AgentLogBatchCodec.TryRead(AgentLogBatchCodec.Serialize(data), out var parsed));
        Assert.IsNotNull(parsed);
        Assert.AreEqual(gap, parsed.Items[0]);
        Assert.AreEqual(1L, parsed.Items[1].Gap!.AffectedCount);
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogBatchCodec.Serialize(batch with { Items = [gap with { Content = "event" }] }));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogBatchCodec.Serialize(batch with { Items = [gap with { Gap = new(AgentLogGapReason.Oversized, 0) }] }));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogBatchCodec.Serialize(batch with { Items = [gap with { Gap = new((AgentLogGapReason)999, null) }] }));
    }

    [TestMethod]
    public void Codec_PreservesMultilineContentAndMissingSourceTime()
    {
        var batch = CreateBatch() with
        {
            Items = [new(Guid.CreateVersion7(), null, "服務啟動\nsecond line\t✓", null, null, null, null, null)]
        };
        var bytes = AgentLogBatchCodec.Serialize(batch);
        Assert.IsTrue(AgentLogBatchCodec.TryRead(bytes, out var parsed));
        Assert.IsNotNull(parsed);
        Assert.AreEqual(batch.SourceId, parsed.SourceId);
        Assert.AreEqual(batch.Items[0], parsed.Items[0]);
    }

    [TestMethod]
    public void Codec_RejectsDuplicateIdsAndInvalidMetadata()
    {
        var batch = CreateBatch();
        var item = batch.Items[0];
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogBatchCodec.Serialize(batch with { Items = [item, item] }));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogBatchCodec.Serialize(batch with { SourceId = Guid.NewGuid() }));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogBatchCodec.Serialize(batch with { SourceName = "source\nother" }));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogBatchCodec.Serialize(batch with { Items = [item with { EventId = -1 }] }));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogBatchCodec.Serialize(batch with { Items = [item with { OccurredAt = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(1)) }] }));
    }

    [TestMethod]
    public void Codec_RejectsOversizedOrMalformedInput()
    {
        var batch = CreateBatch();
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogBatchCodec.Serialize(batch with
        {
            Items = [batch.Items[0] with { Content = new string('x', AgentLogBatchCodec.MaximumContentCharacters + 1) }]
        }));
        Assert.ThrowsExactly<ArgumentException>(() => AgentLogBatchCodec.Serialize(batch with
        {
            Items = Enumerable.Range(0, AgentLogBatchCodec.MaximumItems + 1)
                .Select(_ => batch.Items[0] with { MessageId = Guid.CreateVersion7() }).ToArray()
        }));
        Assert.IsFalse(AgentLogBatchCodec.TryRead("{\"schemaVersion\":3}"u8, out _));
        Assert.IsFalse(AgentLogBatchCodec.TryRead(new byte[AgentBatchCodec.MaximumBytes + 1], out _));
    }

    [TestMethod]
    public void Codec_EnforcesLimitsWhileReadingUntrustedJson()
    {
        var batch = CreateBatch();
        var maximum = batch with { Items = [batch.Items[0] with { Content = new string('x', AgentLogBatchCodec.MaximumContentCharacters) }] };
        Assert.IsTrue(AgentLogBatchCodec.TryRead(AgentLogBatchCodec.Serialize(maximum), out _));

        var tooMany = batch with
        {
            Items = Enumerable.Range(0, AgentLogBatchCodec.MaximumItems + 1)
                .Select(_ => batch.Items[0] with { MessageId = Guid.CreateVersion7() }).ToArray()
        };
        var raw = JsonSerializer.SerializeToUtf8Bytes(tooMany, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsFalse(AgentLogBatchCodec.TryRead(raw, out _));
        var valid = Encoding.UTF8.GetString(AgentLogBatchCodec.Serialize(batch));
        Assert.IsFalse(AgentLogBatchCodec.TryRead(Encoding.UTF8.GetBytes(valid.Insert(1, "\"unexpected\":true,")), out _));
        Assert.IsFalse(AgentLogBatchCodec.TryRead(Encoding.UTF8.GetBytes(valid.Insert(1, "\"schemaVersion\":3,")), out _));
    }

    private static AgentLogBatch CreateBatch()
    {
        var now = DateTimeOffset.UtcNow;
        return new(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), now, Guid.CreateVersion7(),
            AgentLogSourceKind.Journal, "system", 1,
            [new(Guid.CreateVersion7(), now, "event", "provider", 42, "warning", "user", "process")]);
    }
}

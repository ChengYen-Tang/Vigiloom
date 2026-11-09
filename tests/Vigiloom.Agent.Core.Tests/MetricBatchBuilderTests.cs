using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class MetricBatchBuilderTests
{
    [TestMethod]
    public void Create_PreservesPointsAcrossByteBoundedBatches()
    {
        var now = DateTimeOffset.UtcNow;
        // Non-ASCII characters are escaped by System.Text.Json's default encoder.
        var source = new string('測', 128);
        var dimension = new string('試', 256);
        var input = Enumerable.Range(0, 501).Select(i => new AgentMetricPoint(Guid.CreateVersion7(), now,
            AgentMetricKind.ProcessWorkingSetBytes, source, dimension, i)).ToArray();
        var batches = MetricBatchBuilder.Create(input, now);
        Assert.HasCount(3, batches);
        var decoded = new List<AgentMetricPoint>();
        foreach (var batch in batches)
        {
            Assert.IsLessThanOrEqualTo(DurableBatchQueue.MaximumBatchBytes, batch.Payload.Length);
            Assert.IsLessThanOrEqualTo(MetricBatchBuilder.MaximumPointsPerBatch, batch.RecordCount);
            Assert.IsTrue(AgentMetricBatchCodec.TryRead(batch.Payload, out var actual));
            Assert.IsNotNull(actual);
            Assert.AreEqual(batch.BatchId, actual.BatchId);
            Assert.AreEqual(batch.EnqueuedAt, actual.EnqueuedAt);
            decoded.AddRange(actual.Items);
        }
        CollectionAssert.AreEqual(input.Select(p => p.MessageId).ToArray(), decoded.Select(p => p.MessageId).ToArray());
    }

    [TestMethod]
    public void Create_EmptyCollectionProducesNoBatches()
    {
        Assert.IsEmpty(MetricBatchBuilder.Create([], DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void Create_InvalidPointFailsInsteadOfOmittingIt()
    {
        var now = DateTimeOffset.UtcNow;
        var invalid = new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.MemoryTotalBytes, "", null, 1);
        Assert.Throws<ArgumentException>(() => MetricBatchBuilder.Create([invalid], now));
    }
}

using Vigiloom.Contracts.Agent.Baseline;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class BaselineBatchTests
{
    [TestMethod]
    public void RoundTrip_PreservesDistinctOutcomesAndSourceRevision()
    {
        var now = DateTimeOffset.UtcNow;
        var sourceId = Guid.CreateVersion7();
        var revisionId = Guid.CreateVersion7();
        var items = Enum.GetValues<BaselineOutcome>().Select(outcome =>
            new BaselineResult(sourceId, revisionId, "AQID", $"check-{outcome}", now, "ubuntu:24.04", outcome, null)).ToArray();
        var batch = new BaselineBatch(BaselineBatchCodec.SchemaVersion, Guid.CreateVersion7(), now, items);

        Assert.IsTrue(BaselineBatchCodec.TryRead(BaselineBatchCodec.Serialize(batch), out var decoded));
        Assert.IsNotNull(decoded);
        CollectionAssert.AreEqual(items, decoded.Items.ToArray());
        Assert.AreEqual(batch.BatchId, decoded.BatchId);
    }

    [TestMethod]
    public void Serialize_RejectsInvalidOutcomeAndOversizedEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new BaselineResult(Guid.CreateVersion7(), Guid.CreateVersion7(), "AQID", "check", now,
            "ubuntu:24.04", BaselineOutcome.Pass, null);
        var batch = new BaselineBatch(BaselineBatchCodec.SchemaVersion, Guid.CreateVersion7(), now, [item]);

        Assert.Throws<ArgumentException>(() => BaselineBatchCodec.Serialize(batch with { Items = [item with { Outcome = (BaselineOutcome)99 }] }));
        Assert.Throws<ArgumentException>(() => BaselineBatchCodec.Serialize(batch with { Items = [item with { Evidence = new string('x', 513) }] }));
    }
}

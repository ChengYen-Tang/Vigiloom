using Vigiloom.Contracts.Agent;

namespace Vigiloom.Contracts.Tests;

[TestClass]
public sealed class AgentUpdateResultTests
{
    [TestMethod]
    public void ResultRequiresValidIdentifiersAndConsistentSuccess()
    {
        var result = new AgentUpdateResult(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            new string('a', 64), 1, AgentUpdateResultKind.Succeeded, DateTimeOffset.UtcNow, false, null);
        Assert.IsTrue(result.IsValid());
        Assert.IsFalse((result with { MessageId = Guid.Empty }).IsValid());
        Assert.IsFalse((result with { Sequence = 0 }).IsValid());
        Assert.IsFalse((result with { ManifestSha256 = new string('A', 64) }).IsValid());
        Assert.IsFalse((result with { RetryAllowed = true }).IsValid());
        Assert.IsFalse((result with { Failure = AgentUpdateFailure.Other }).IsValid());
        Assert.IsFalse((result with { OccurredAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)) }).IsValid());
        Assert.IsTrue((result with { Result = AgentUpdateResultKind.RolledBack, RetryAllowed = true, Failure = AgentUpdateFailure.Interrupted }).IsValid());
    }
}

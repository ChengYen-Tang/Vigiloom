using Vigiloom.Modules.SecurityAnalysis.Application.Detection.Correlation;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Detection;

[TestClass]
public sealed class EventCountTests(TestContext context)
{
    [TestMethod]
    public void Evaluate_UsesEventTimeGroupsAndDeduplicatesRetries()
    {
        var start = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        var agent = Guid.CreateVersion7();
        var first = new CountEvent(agent, Guid.CreateVersion7(), start, start.AddMinutes(2), ["alice", "domain"]);
        var second = new CountEvent(agent, Guid.CreateVersion7(), start.AddMinutes(1), start.AddMinutes(1), ["alice", "domain"]);
        var third = new CountEvent(agent, Guid.CreateVersion7(), start.AddMinutes(2), start.AddMinutes(2), ["alice", "domain"]);
        var other = new CountEvent(agent, Guid.CreateVersion7(), start.AddMinutes(2), start.AddMinutes(2), ["bob", "domain"]);
        var result = EventCount.Evaluate([third, first, second, second, other], TimeSpan.FromMinutes(2), 3,
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), 20, context.CancellationToken);

        var match = Assert.ContainsSingle(result.Matches);
        Assert.AreEqual(third.MessageId, match.MessageId);
        Assert.AreEqual(3, match.Count);
        Assert.AreEqual(start, match.FirstEventAt);
        Assert.IsEmpty(result.Unassessed);
        Assert.IsFalse(result.CapacityExceeded);
    }

    [TestMethod]
    public void Evaluate_MissingTimeLateOrFutureEvents_AreUnassessed()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        var agent = Guid.CreateVersion7();
        var missing = new CountEvent(agent, Guid.CreateVersion7(), null, now, ["alice"]);
        var late = new CountEvent(agent, Guid.CreateVersion7(), now.AddMinutes(-6), now, ["alice"]);
        var future = new CountEvent(agent, Guid.CreateVersion7(), now.AddMinutes(1), now, ["alice"]);
        var noGroup = new CountEvent(agent, Guid.CreateVersion7(), now, now, []);
        var result = EventCount.Evaluate([missing, late, future, noGroup], TimeSpan.FromMinutes(5), 1,
            TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), 20, context.CancellationToken);

        Assert.IsEmpty(result.Matches);
        Assert.HasCount(4, result.Unassessed);
    }

    [TestMethod]
    public void Evaluate_CapacityLimit_DoesNotReturnPartialResults()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        var events = Enumerable.Range(0, 3).Select(_ => new CountEvent(Guid.CreateVersion7(), Guid.CreateVersion7(), now, now, ["alice"])).ToArray();
        var result = EventCount.Evaluate(events, TimeSpan.FromMinutes(1), 1, TimeSpan.Zero, TimeSpan.Zero, 2, context.CancellationToken);
        Assert.IsTrue(result.CapacityExceeded);
        Assert.IsEmpty(result.Matches);
        Assert.IsEmpty(result.Unassessed);
    }

    [TestMethod]
    public void Evaluate_GroupFields_DoNotCollideWhenValuesContainSeparators()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        var agent = Guid.CreateVersion7();
        var first = new CountEvent(agent, Guid.CreateVersion7(), now, now, ["a|b", "c"]);
        var second = new CountEvent(agent, Guid.CreateVersion7(), now, now, ["a", "b|c"]);
        var result = EventCount.Evaluate([first, second], TimeSpan.FromSeconds(1), 2, TimeSpan.Zero, TimeSpan.Zero, 2, context.CancellationToken);
        Assert.IsEmpty(result.Matches);
    }

    [TestMethod]
    public void Evaluate_ObservedEmptyGroupValue_IsNotMissing()
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00+00:00");
        var agent = Guid.CreateVersion7();
        var first = new CountEvent(agent, Guid.CreateVersion7(), now, now, [""]);
        var second = new CountEvent(agent, Guid.CreateVersion7(), now, now, [""]);
        var result = EventCount.Evaluate([first, second], TimeSpan.FromSeconds(1), 2, TimeSpan.Zero, TimeSpan.Zero, 2, context.CancellationToken);
        Assert.HasCount(1, result.Matches);
        Assert.IsEmpty(result.Unassessed);
    }
}

using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
public sealed class HealthTimelineTests
{
    [TestMethod]
    public void Aggregate_UsesDurationsAndUnknownCoverageInsteadOfCountingChecks()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var target = Guid.CreateVersion7();
        var result = HealthTimeline.Aggregate(start, start.AddMinutes(60),
        [
            new(target, EvaluatedHealth.Healthy, start, start.AddMinutes(45)),
            new(target, EvaluatedHealth.Degraded, start.AddMinutes(45), start.AddMinutes(50)),
            new(target, EvaluatedHealth.Unhealthy, start.AddMinutes(50), start.AddMinutes(55))
        ]);
        Assert.AreEqual(TimeSpan.FromMinutes(45), result.Healthy);
        Assert.AreEqual(TimeSpan.FromMinutes(5), result.Unknown);
        Assert.AreEqual(45d / 55d, result.Availability!.Value, 0.000001);
        Assert.AreEqual(55d / 60d, result.Coverage, 0.000001);
    }

    [TestMethod]
    public void Aggregate_CombinesConcurrentInstancesAndPreservesUnknown()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var a = Guid.CreateVersion7(); var b = Guid.CreateVersion7();
        var result = HealthTimeline.Aggregate(start, start.AddMinutes(30),
        [
            new(a, EvaluatedHealth.Healthy, start, start.AddMinutes(30)),
            new(b, EvaluatedHealth.Unknown, start, start.AddMinutes(10)),
            new(b, EvaluatedHealth.Unhealthy, start.AddMinutes(10), start.AddMinutes(20)),
            new(b, EvaluatedHealth.Healthy, start.AddMinutes(20), start.AddMinutes(30))
        ]);
        Assert.HasCount(3, result.Segments);
        Assert.AreEqual(EvaluatedHealth.Unknown, result.Segments[0].State);
        Assert.AreEqual(EvaluatedHealth.Degraded, result.Segments[1].State);
        Assert.AreEqual(EvaluatedHealth.Healthy, result.Segments[2].State);
        Assert.AreEqual(0.5, result.Availability!.Value, 0.000001);
        Assert.AreEqual(2d / 3d, result.Coverage, 0.000001);
    }

    [TestMethod]
    public void Aggregate_NoParticipants_ReturnsUnknownWithoutAvailability()
    {
        var start = DateTimeOffset.UnixEpoch;
        var result = HealthTimeline.Aggregate(start, start.AddHours(1), []);
        Assert.AreEqual(TimeSpan.FromHours(1), result.Unknown);
        Assert.IsNull(result.Availability);
        Assert.AreEqual(0d, result.Coverage);
    }
}

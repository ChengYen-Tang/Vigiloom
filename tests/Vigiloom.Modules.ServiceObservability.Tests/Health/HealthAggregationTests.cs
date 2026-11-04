using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
public sealed class HealthAggregationTests
{
    [TestMethod]
    public void Combine_EmptyOrUnknownParticipants_DoesNotClaimHealthy()
    {
        Assert.AreEqual(new HealthAggregate(EvaluatedHealth.Unknown, 0, 0), HealthAggregation.Combine([]));
        Assert.AreEqual(new HealthAggregate(EvaluatedHealth.Unknown, 2, 1),
            HealthAggregation.Combine([EvaluatedHealth.Healthy, EvaluatedHealth.Unknown]));
        Assert.AreEqual(new HealthAggregate(EvaluatedHealth.Unknown, 2, 1),
            HealthAggregation.Combine([EvaluatedHealth.Unhealthy, EvaluatedHealth.Unknown]));
    }

    [TestMethod]
    public void Combine_KnownMixedOutcomes_ReportsDegradedAndUnknownCount()
    {
        Assert.AreEqual(new HealthAggregate(EvaluatedHealth.Degraded, 3, 1),
            HealthAggregation.Combine([EvaluatedHealth.Healthy, EvaluatedHealth.Unhealthy, EvaluatedHealth.Unknown]));
        Assert.AreEqual(new HealthAggregate(EvaluatedHealth.Degraded, 2, 1),
            HealthAggregation.Combine([EvaluatedHealth.Degraded, EvaluatedHealth.Unknown]));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(3)]
    public void Combine_UniformKnownOutcomes_PreservesState(int value)
    {
        var state = (EvaluatedHealth)value;
        Assert.AreEqual(new HealthAggregate(state, 2, 0), HealthAggregation.Combine([state, state]));
    }
}

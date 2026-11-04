using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Domain.Health;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
public sealed class HealthEvaluationTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void TwoHealthyAndThreeUnhealthy_ApplyThresholdsAndRecover()
    {
        var state = HealthEvaluationState.Initial;
        state = Apply(state, HealthObservationKind.Healthy, 0).State;
        Assert.AreEqual(EvaluatedHealth.Unknown, state.State);
        state = Apply(state, HealthObservationKind.Healthy, 30).State;
        Assert.AreEqual(EvaluatedHealth.Healthy, state.State);
        state = Apply(state, HealthObservationKind.Unhealthy, 60).State;
        state = Apply(state, HealthObservationKind.Unhealthy, 90).State;
        Assert.AreEqual(EvaluatedHealth.Healthy, state.State);
        var third = Apply(state, HealthObservationKind.Unhealthy, 120);
        Assert.AreEqual(EvaluatedHealth.Unhealthy, third.State.State);
        Assert.IsTrue(third.ChangedAtObservation);
        state = Apply(third.State, HealthObservationKind.Healthy, 150).State;
        Assert.AreEqual(EvaluatedHealth.Unhealthy, state.State);
        state = Apply(state, HealthObservationKind.Healthy, 180).State;
        Assert.AreEqual(EvaluatedHealth.Healthy, state.State);
    }

    [TestMethod]
    public void AlternatingUnhealthyAndTimeout_CannotKeepOldHealthyEvidence()
    {
        var state = Apply(Apply(HealthEvaluationState.Initial, HealthObservationKind.Healthy, 0).State,
            HealthObservationKind.Healthy, 30).State;
        state = Apply(state, HealthObservationKind.Unhealthy, 60).State;
        state = Apply(state, HealthObservationKind.Timeout, 90).State;
        var expired = Apply(state, HealthObservationKind.Unhealthy, 120);
        Assert.AreEqual(EvaluatedHealth.Unknown, expired.State.State);
        Assert.AreEqual(Start.AddSeconds(95), expired.ExpiredAt);
    }

    [TestMethod]
    public void SuccessStreakAfterLongGap_DoesNotUseOldSuccess()
    {
        var state = Apply(HealthEvaluationState.Initial, HealthObservationKind.Healthy, 0).State;
        var afterGap = Apply(state, HealthObservationKind.Healthy, 3600);
        Assert.AreEqual(EvaluatedHealth.Unknown, afterGap.State.State);
        Assert.AreEqual(1, afterGap.State.StreakCount);
        Assert.AreEqual(EvaluatedHealth.Healthy, Apply(afterGap.State, HealthObservationKind.Healthy, 3630).State.State);
    }

    [TestMethod]
    public void ObservationFailuresBecomeUnknown_NotApplicationOutage()
    {
        var state = Apply(Apply(HealthEvaluationState.Initial, HealthObservationKind.Healthy, 0).State,
            HealthObservationKind.Healthy, 30).State;
        state = Apply(state, HealthObservationKind.Timeout, 40).State;
        state = Apply(state, HealthObservationKind.TransportError, 50).State;
        state = Apply(state, HealthObservationKind.NetworkDenied, 60).State;
        Assert.AreEqual(EvaluatedHealth.Unknown, state.State);
    }

    [TestMethod]
    public void DegradedIsImmediate_AndOlderOrDuplicateObservationsAreIgnored()
    {
        var degraded = Apply(HealthEvaluationState.Initial, HealthObservationKind.Degraded, 30);
        Assert.AreEqual(EvaluatedHealth.Degraded, degraded.State.State);
        Assert.IsTrue(degraded.ChangedAtObservation);
        var old = Apply(degraded.State, HealthObservationKind.Healthy, 29);
        Assert.IsTrue(old.Ignored);
        Assert.AreEqual(degraded.State, old.State);
        Assert.IsTrue(Apply(degraded.State, HealthObservationKind.Healthy, 30).Ignored);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(10)]
    public void RecoveryThreshold_IsHonouredWhenPreviousStateExpires(int threshold)
    {
        var state = HealthEvaluationState.Initial;
        for (var i = 0; i < 3; i++)
            state = HealthEvaluation.Apply(state, HealthObservationKind.Unhealthy, Start.AddSeconds(i * 30), 30, 5, 3, threshold).State;
        Assert.AreEqual(EvaluatedHealth.Unhealthy, state.State);
        for (var i = 1; i <= threshold; i++)
        {
            var result = HealthEvaluation.Apply(state, HealthObservationKind.Healthy, Start.AddSeconds(60 + i * 30), 30, 5, 3, threshold);
            state = result.State;
            if (i < threshold) Assert.AreNotEqual(EvaluatedHealth.Healthy, state.State);
        }
        Assert.AreEqual(EvaluatedHealth.Healthy, state.State);
    }

    [TestMethod]
    [DataRow(64, 2)]
    [DataRow(65, 1)]
    [DataRow(66, 1)]
    public void SuccessStreak_ExpiresAtFreshnessBoundary(int gap, int expectedCount)
    {
        var first = Apply(HealthEvaluationState.Initial, HealthObservationKind.Healthy, 0).State;
        var second = HealthEvaluation.Apply(first, HealthObservationKind.Healthy, Start.AddSeconds(gap), 30, 5, 3, 3);
        Assert.AreEqual(expectedCount, second.State.StreakCount);
        Assert.AreEqual(EvaluatedHealth.Unknown, second.State.State);
    }

    [TestMethod]
    public void Expire_WithoutNewObservation_BecomesUnknownAtBoundary()
    {
        var state = Apply(Apply(HealthEvaluationState.Initial, HealthObservationKind.Healthy, 0).State,
            HealthObservationKind.Healthy, 30).State;
        Assert.AreEqual(EvaluatedHealth.Healthy, HealthEvaluation.Expire(state, Start.AddSeconds(94), 30, 5).State.State);
        var expired = HealthEvaluation.Expire(state, Start.AddSeconds(95), 30, 5);
        Assert.AreEqual(EvaluatedHealth.Unknown, expired.State.State);
        Assert.AreEqual(Start.AddSeconds(95), expired.ExpiredAt);
    }

    private static HealthEvaluationResult Apply(HealthEvaluationState state, HealthObservationKind kind, int seconds) =>
        HealthEvaluation.Apply(state, kind, Start.AddSeconds(seconds), 30, 5, 3, 2);
}

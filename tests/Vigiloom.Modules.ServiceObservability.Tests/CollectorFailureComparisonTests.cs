using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Modules.ServiceObservability.Tests;

[TestClass]
public sealed class CollectorFailureComparisonTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMinutes(5);
    private static ulong UnixNanos(DateTimeOffset value) => (ulong)value.ToUnixTimeMilliseconds() * 1_000_000;
    private static FailureSample Sample(int measuredSeconds, long value, int startSeconds = -600) =>
        new(UnixNanos(Now.AddSeconds(measuredSeconds)), UnixNanos(Now.AddSeconds(startSeconds)), Now.AddSeconds(measuredSeconds), true, false, value, null);
    private static FailureComparison Compare(FailureSample[] samples, bool complete = true, bool active = true) =>
        CollectorFailureComparison.Evaluate(samples, Now, complete, active, Freshness, MaximumGap);

    [TestMethod]
    public void EvaluatesOrderedDistinctSamplesAndDeduplicatesIdenticalRetransmissions()
    {
        var before = Sample(-45, 8);
        var latest = Sample(-15, 10);
        var retransmission = latest with { ReceivedAt = Now.AddSeconds(-5) };
        var increased = Compare([before, retransmission, latest]);
        Assert.AreEqual(FailureObservation.Increased, increased.Observation);
        Assert.AreEqual(before.MeasuredAt, increased.From);
        Assert.AreEqual(latest.MeasuredAt, increased.Through);
        Assert.AreEqual(FailureObservation.Unchanged, Compare([before, latest with { Integer = 8 }]).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([latest, retransmission]).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([before, latest, latest with { Integer = 11 }]).Observation);
    }

    [TestMethod]
    public void IncompleteInvalidRestartAndDecreasingCountersRemainUnknown()
    {
        var before = Sample(-45, 8);
        var latest = Sample(-15, 10);
        Assert.AreEqual(FailureObservation.Unknown, Compare([], complete: true).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([before, latest], complete: false).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([before, latest], active: false).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([before, latest with { Missing = true }]).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([before, latest with { Supported = false }]).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([before, latest with { StartedAt = 0 }]).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([before, latest with { StartedAt = UnixNanos(Now.AddSeconds(-20)) }]).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([before, latest with { Integer = 7 }]).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([before, latest with { Integer = null, Floating = double.NaN }]).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([before, latest with { Integer = null, Floating = 10 }]).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([Sample(-400, 3), latest]).Observation);
        Assert.AreEqual(FailureObservation.Unknown, Compare([before, Sample(1, 10)]).Observation);
    }

    [TestMethod]
    public void OldOrReplayedMeasurementsCannotBeMadeFreshByAnotherReceipt()
    {
        var old = Sample(-120, 5);
        Assert.AreEqual(FailureObservation.Stale, Compare([old, old with { ReceivedAt = Now }]).Observation);
        var latest = Sample(-15, 10);
        Assert.AreEqual(FailureObservation.Stale, Compare([latest with { ReceivedAt = Now.AddSeconds(-100) }, latest]).Observation);
    }
}

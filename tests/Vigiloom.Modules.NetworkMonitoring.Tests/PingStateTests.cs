using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Domain;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class PingStateTests
{
    [TestMethod]
    public void ThresholdsAndMonitorErrorsDoNotInventReachability()
    {
        var settings = new PingSettingsSnapshot(30, 2, 3, 2, 64, "");
        var state = new PingTargetState();
        PingStateMachine.Apply(state, new(PingOutcome.Timeout, "127.0.0.1", null), settings);
        Assert.AreEqual(Reachability.Unknown, state.State);
        PingStateMachine.Apply(state, new(PingOutcome.Timeout, "127.0.0.1", null), settings);
        Assert.AreEqual(Reachability.Unknown, state.State);
        PingStateMachine.Apply(state, new(PingOutcome.Timeout, "127.0.0.1", null), settings);
        Assert.AreEqual(Reachability.Unreachable, state.State);
        Assert.IsNull(state.LatencyMilliseconds);
        PingStateMachine.Apply(state, new(PingOutcome.Success, "127.0.0.1", 0), settings);
        Assert.AreEqual(Reachability.Unreachable, state.State);
        PingStateMachine.Apply(state, new(PingOutcome.DnsError, null, null), settings);
        Assert.AreEqual(Reachability.Unknown, state.State);
        Assert.AreEqual(0, state.Failures);
        PingStateMachine.Apply(state, new(PingOutcome.Success, "127.0.0.1", 1), settings);
        Assert.AreEqual(Reachability.Unknown, state.State);
        PingStateMachine.Apply(state, new(PingOutcome.Success, "127.0.0.1", 2), settings);
        Assert.AreEqual(Reachability.Reachable, state.State);
    }

    [TestMethod]
    [DataRow(PingOutcome.DnsError)]
    [DataRow(PingOutcome.PermissionDenied)]
    [DataRow(PingOutcome.MonitorError)]
    public void MonitorFailuresInterruptConsecutiveDeviceFailures(PingOutcome outcome)
    {
        var state = new PingTargetState { State = Reachability.Reachable, Failures = 2 };
        PingStateMachine.Apply(state, new(outcome, null, null), new(30, 2, 3, 2, 64, ""));
        Assert.AreEqual(Reachability.Unknown, state.State);
        Assert.AreEqual(0, state.Failures);
        Assert.AreEqual(0, state.Successes);
    }

    [TestMethod]
    [DataRow("0.0.0.0")]
    [DataRow("::")]
    public async Task UnspecifiedAddressProducesAMonitorResult(string target)
    {
        var measurement = await new SystemPingProbe().MeasureAsync(target, 1, CancellationToken.None);
        Assert.AreEqual(PingOutcome.MonitorError, measurement.Outcome);
        Assert.IsNull(measurement.LatencyMilliseconds);
    }

    [TestMethod]
    [TestCategory("Integration")]
    public async Task NativeProbeMeasuresLoopbackAndRejectsInvalidDns()
    {
        var probe = new SystemPingProbe();
        var measurement = await probe.MeasureAsync("127.0.0.1", 2, CancellationToken.None);
        Assert.AreEqual(PingOutcome.Success, measurement.Outcome);
        Assert.IsNotNull(measurement.LatencyMilliseconds);
        Assert.IsGreaterThanOrEqualTo(0d, measurement.LatencyMilliseconds.Value);
        Assert.AreEqual("127.0.0.1", measurement.Address);
        var invalid = await probe.MeasureAsync("invalid name with spaces", 1, CancellationToken.None);
        Assert.AreEqual(PingOutcome.DnsError, invalid.Outcome);
        Assert.IsNull(invalid.LatencyMilliseconds);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => probe.MeasureAsync("127.0.0.1", 2, cancelled.Token));
    }
}

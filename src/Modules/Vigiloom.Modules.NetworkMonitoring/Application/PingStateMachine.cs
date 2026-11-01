using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Domain;

namespace Vigiloom.Modules.NetworkMonitoring.Application;

public static class PingStateMachine
{
    public static void Apply(PingTargetState state, PingMeasurement measurement, PingSettingsSnapshot settings)
    {
        state.Outcome = measurement.Outcome;
        state.ResolvedAddress = measurement.Address;
        state.LatencyMilliseconds = measurement.LatencyMilliseconds;
        switch (measurement.Outcome)
        {
            case PingOutcome.Success:
                state.Failures = 0; state.Successes = Math.Min(state.Successes + 1, settings.SuccessThreshold);
                if (state.Successes >= settings.SuccessThreshold) state.State = Reachability.Reachable;
                break;
            case PingOutcome.Timeout or PingOutcome.Unreachable:
                state.Successes = 0; state.Failures = Math.Min(state.Failures + 1, settings.FailureThreshold);
                if (state.Failures >= settings.FailureThreshold) state.State = Reachability.Unreachable;
                break;
            default:
                state.Successes = state.Failures = 0;
                state.State = Reachability.Unknown;
                break;
        }
    }
}

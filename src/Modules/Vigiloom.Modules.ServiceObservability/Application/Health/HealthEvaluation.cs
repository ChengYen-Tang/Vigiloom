using Vigiloom.Modules.ServiceObservability.Domain.Health;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

internal enum EvaluatedHealth : byte { Unknown, Healthy, Degraded, Unhealthy }
internal enum HealthStreak : byte { None, Success, ApplicationFailure, ObservationFailure }

internal readonly record struct HealthEvaluationState(EvaluatedHealth State, DateTimeOffset? EvidenceAt,
    HealthStreak Streak, int StreakCount, DateTimeOffset? StreakAt, DateTimeOffset? LastObservationAt)
{
    internal static HealthEvaluationState Initial => new(EvaluatedHealth.Unknown, null, HealthStreak.None, 0, null, null);
}

internal readonly record struct HealthEvaluationResult(HealthEvaluationState State, DateTimeOffset? ExpiredAt,
    bool ChangedAtObservation, bool Ignored);
internal readonly record struct HealthExpiryResult(HealthEvaluationState State, DateTimeOffset? ExpiredAt);

/// <summary>Applies ordered probe observations without deriving application health from transport liveness.</summary>
internal static class HealthEvaluation
{
    internal static HealthEvaluationResult Apply(HealthEvaluationState previous, HealthObservationKind outcome,
        DateTimeOffset at, int intervalSeconds, int timeoutSeconds, int failureThreshold, int recoveryThreshold)
    {
        if (at.Offset != TimeSpan.Zero || intervalSeconds is < 5 or > 3600 || timeoutSeconds is < 1 or > 60 ||
            timeoutSeconds > intervalSeconds || failureThreshold is < 1 or > 10 || recoveryThreshold is < 1 or > 10 ||
            outcome is < HealthObservationKind.Healthy or > HealthObservationKind.NetworkDenied)
            throw new ArgumentException("Invalid health evaluation input.");
        if (previous.LastObservationAt is { } last && at <= last) return new(previous, null, false, true);

        var expiry = Expire(previous, at, intervalSeconds, timeoutSeconds);
        var state = expiry.State;

        var before = state.State;
        if (outcome == HealthObservationKind.Degraded)
        {
            state = state with { State = EvaluatedHealth.Degraded, EvidenceAt = at, Streak = HealthStreak.None, StreakCount = 0, StreakAt = null };
        }
        else
        {
            var streak = outcome switch
            {
                HealthObservationKind.Healthy => HealthStreak.Success,
                HealthObservationKind.Unhealthy => HealthStreak.ApplicationFailure,
                _ => HealthStreak.ObservationFailure
            };
            var threshold = streak == HealthStreak.Success ? recoveryThreshold : failureThreshold;
            var count = state.Streak == streak ? Math.Min(state.StreakCount + 1, threshold) : 1;
            state = state with { Streak = streak, StreakCount = count, StreakAt = at };
            if (streak == HealthStreak.Success && (state.State == EvaluatedHealth.Healthy || count >= threshold))
                state = state with { State = EvaluatedHealth.Healthy, EvidenceAt = at };
            else if (streak == HealthStreak.ApplicationFailure && (state.State == EvaluatedHealth.Unhealthy || count >= threshold))
                state = state with { State = EvaluatedHealth.Unhealthy, EvidenceAt = at };
            else if (streak == HealthStreak.ObservationFailure && count >= threshold)
                state = state with { State = EvaluatedHealth.Unknown, EvidenceAt = null };
        }
        state = state with { LastObservationAt = at };
        return new(state, expiry.ExpiredAt, state.State != before, false);
    }

    internal static HealthExpiryResult Expire(HealthEvaluationState previous, DateTimeOffset at, int intervalSeconds, int timeoutSeconds)
    {
        if (at.Offset != TimeSpan.Zero || intervalSeconds is < 5 or > 3600 || timeoutSeconds is < 1 or > 60 || timeoutSeconds > intervalSeconds)
            throw new ArgumentException("Invalid health expiry input.");
        if (previous.LastObservationAt is { } last && at < last) return new(previous, null);
        var freshness = TimeSpan.FromSeconds(2 * intervalSeconds + timeoutSeconds);
        var state = previous;
        DateTimeOffset? expiredAt = null;
        if (state.State != EvaluatedHealth.Unknown && state.EvidenceAt is { } evidence && evidence + freshness <= at)
        {
            expiredAt = evidence + freshness;
            state = state with { State = EvaluatedHealth.Unknown, EvidenceAt = null };
        }
        if (state.StreakAt is { } streakAt && streakAt + freshness <= at)
            state = state with { Streak = HealthStreak.None, StreakCount = 0, StreakAt = null };
        return new(state, expiredAt);
    }
}

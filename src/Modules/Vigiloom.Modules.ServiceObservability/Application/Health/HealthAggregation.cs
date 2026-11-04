namespace Vigiloom.Modules.ServiceObservability.Application.Health;

internal readonly record struct HealthAggregate(EvaluatedHealth State, int Participants, int Unknown);

/// <summary>Aggregates explicitly configured targets without treating missing evidence as healthy.</summary>
internal static class HealthAggregation
{
    internal static HealthAggregate Combine(IEnumerable<EvaluatedHealth> states)
    {
        var total = 0;
        var unknown = 0;
        var healthy = 0;
        var unhealthy = 0;
        var degraded = 0;
        foreach (var state in states)
        {
            total++;
            switch (state)
            {
                case EvaluatedHealth.Unknown: unknown++; break;
                case EvaluatedHealth.Healthy: healthy++; break;
                case EvaluatedHealth.Degraded: degraded++; break;
                case EvaluatedHealth.Unhealthy: unhealthy++; break;
                default: throw new ArgumentOutOfRangeException(nameof(states), "Unknown health state.");
            }
        }
        var aggregate = degraded > 0 || (healthy > 0 && unhealthy > 0) ? EvaluatedHealth.Degraded
            : total == 0 || unknown > 0 ? EvaluatedHealth.Unknown
            : unhealthy > 0 ? EvaluatedHealth.Unhealthy : EvaluatedHealth.Healthy;
        return new(aggregate, total, unknown);
    }
}

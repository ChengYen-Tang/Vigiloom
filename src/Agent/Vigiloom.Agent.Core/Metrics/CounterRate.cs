namespace Vigiloom.Agent.Core.Metrics;

/// <summary>Converts two monotonic counter samples to a per-second rate.</summary>
public static class CounterRate
{
    /// <summary>
    /// Returns no rate for the first sample, a reset or wrap, or an invalid or stale interval.
    /// Elapsed time must be measured using a monotonic clock for samples of the same source.
    /// </summary>
    public static double? PerSecond(ulong? previous, ulong current, TimeSpan elapsed, TimeSpan maximumInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumInterval, TimeSpan.Zero);
        if (previous is null || current < previous.Value || elapsed <= TimeSpan.Zero || elapsed > maximumInterval)
            return null;

        return (current - previous.Value) / elapsed.TotalSeconds;
    }
}

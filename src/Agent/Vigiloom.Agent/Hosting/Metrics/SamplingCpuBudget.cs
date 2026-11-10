namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>Cooperative CPU allowance for starting new collection work, measured per logical CPU.</summary>
internal sealed class SamplingCpuBudget(TimeProvider clock, TimeSpan window, TimeSpan allowance)
{
    private long? windowStart;
    private TimeSpan consumed;

    public void SetAllowance(TimeSpan value)
    {
        if (value <= TimeSpan.Zero || value > window) throw new ArgumentOutOfRangeException(nameof(value));
        allowance = value;
    }

    public TimeSpan Delay()
    {
        if (window <= TimeSpan.Zero || allowance <= TimeSpan.Zero || allowance > window)
            throw new InvalidOperationException("Invalid sampling CPU budget.");
        var now = clock.GetTimestamp();
        if (windowStart is null) { windowStart = now; return TimeSpan.Zero; }
        var elapsed = clock.GetElapsedTime(windowStart.Value, now);
        if (elapsed < TimeSpan.Zero || elapsed >= window)
        {
            windowStart = now;
            consumed = TimeSpan.Zero;
            return TimeSpan.Zero;
        }
        return consumed >= allowance ? window - elapsed : TimeSpan.Zero;
    }

    public void Record(TimeSpan before, TimeSpan after)
    {
        if (before < TimeSpan.Zero || after < before) throw new InvalidOperationException("The Agent CPU counter is unavailable or reset.");
        // A cycle can cross a window boundary; charge its full cost to the
        // completion window instead of immediately discarding it on rollover.
        _ = Delay();
        var elapsed = after - before;
        consumed = elapsed >= TimeSpan.MaxValue - consumed ? TimeSpan.MaxValue : consumed + elapsed;
    }

    public void MarkUnknown() { _ = Delay(); consumed = TimeSpan.MaxValue; }
}

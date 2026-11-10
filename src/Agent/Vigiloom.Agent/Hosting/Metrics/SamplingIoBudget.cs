using Vigiloom.Agent.Core.Metrics;

namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>Cooperative budget for process I/O observed during collection cycles.</summary>
internal sealed class SamplingIoBudget(TimeProvider clock, TimeSpan window, ulong allowance)
{
    private long? windowStart;
    private ulong consumed;

    public void SetAllowance(ulong value)
    {
        ArgumentOutOfRangeException.ThrowIfZero(value);
        allowance = value;
    }

    public TimeSpan Delay()
    {
        if (window <= TimeSpan.Zero || allowance == 0) throw new InvalidOperationException("Invalid sampling I/O budget.");
        var now = clock.GetTimestamp();
        if (windowStart is null) { windowStart = now; return TimeSpan.Zero; }
        var elapsed = clock.GetElapsedTime(windowStart.Value, now);
        if (elapsed < TimeSpan.Zero || elapsed >= window)
        {
            windowStart = now;
            consumed = 0;
            return TimeSpan.Zero;
        }
        return consumed >= allowance ? window - elapsed : TimeSpan.Zero;
    }

    public void Record(ProcessIoSnapshot before, ProcessIoSnapshot after)
    {
        if (after.ReadBytes < before.ReadBytes || after.WriteBytes < before.WriteBytes)
            throw new InvalidOperationException("The Agent I/O counters reset.");
        _ = Delay();
        var read = after.ReadBytes - before.ReadBytes;
        var write = after.WriteBytes - before.WriteBytes;
        var total = read > ulong.MaxValue - write ? ulong.MaxValue : read + write;
        consumed = total > ulong.MaxValue - consumed ? ulong.MaxValue : consumed + total;
    }

    public void MarkUnknown() { _ = Delay(); consumed = ulong.MaxValue; }
}

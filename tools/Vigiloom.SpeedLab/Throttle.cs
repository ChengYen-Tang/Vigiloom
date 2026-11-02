using System.Diagnostics;

namespace Vigiloom.SpeedLab;

// One shared scheduler per direction keeps concurrent requests within one simulated bottleneck.
internal sealed class Throttle(long bitsPerSecond)
{
    private readonly object gate = new();
    private long next, first, last, bytes;

    public long Bytes { get { lock (gate) return bytes; } }
    public double ObservedMbps
    {
        get
        {
            lock (gate)
                return first == 0 || last == first ? 0 : bytes * 8d / ((last - first) / (double)Stopwatch.Frequency) / 1_000_000d;
        }
    }

    public async ValueTask TransferAsync(int length, CancellationToken token)
    {
        long due;
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            due = Math.Max(next, now) + (long)Math.Ceiling(length * 8d * Stopwatch.Frequency / bitsPerSecond);
            next = due;
        }
        var remaining = due - Stopwatch.GetTimestamp();
        // Permit up to one millisecond of burst to avoid timer precision dominating small writes.
        if (remaining > Stopwatch.Frequency / 1000)
            await Task.Delay(TimeSpan.FromSeconds(remaining / (double)Stopwatch.Frequency), token);
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp();
            if (first == 0) first = now;
            last = now;
            bytes += length;
        }
    }
}

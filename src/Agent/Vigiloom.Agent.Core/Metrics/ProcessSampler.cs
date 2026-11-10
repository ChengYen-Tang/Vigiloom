using System.ComponentModel;
using System.Diagnostics;

namespace Vigiloom.Agent.Core.Metrics;

public sealed record ProcessCounters(int Id, long StartedUtcTicks, TimeSpan CpuTime, long WorkingSetBytes);
public sealed record ProcessRate(ProcessCounters Counters, double? CpuPercent);
public sealed record ProcessSample(IReadOnlyList<ProcessRate> Processes, int UnavailableProcesses, bool Truncated, bool BudgetExhausted = false);

/// <summary>Samples at most 256 processes per pass; CPU percent is relative to one logical CPU.</summary>
public sealed class ProcessSampler(TimeProvider clock)
{
    public const int MaximumProcesses = 256;
    private readonly ProcessRateTracker tracker = new(clock);

    public void Reset() => tracker.Reset();

    public ProcessSample Sample(TimeSpan maximumInterval, CancellationToken token, TimeSpan? collectionBudget = null)
    {
        token.ThrowIfCancellationRequested();
        var budget = collectionBudget ?? TimeSpan.FromSeconds(2);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        var startedAt = clock.GetTimestamp();
        var processes = Process.GetProcesses();
        try
        {
            Array.Sort(processes, static (a, b) => a.Id.CompareTo(b.Id));
            var samples = new List<ProcessCounters>(Math.Min(processes.Length, MaximumProcesses));
            var unavailable = 0;
            var exhausted = false;
            for (var i = 0; i < processes.Length && i < MaximumProcesses; i++)
            {
                token.ThrowIfCancellationRequested();
                var elapsed = clock.GetElapsedTime(startedAt, clock.GetTimestamp());
                if (elapsed < TimeSpan.Zero || elapsed >= budget) { exhausted = true; break; }
                try
                {
                    var process = processes[i];
                    // Clear the enumeration snapshot before reading and recheck the
                    // process instance after the individual OS property reads.
                    process.Refresh();
                    var started = process.StartTime.ToUniversalTime().Ticks;
                    var cpu = process.TotalProcessorTime;
                    var workingSet = process.WorkingSet64;
                    // StartTime is cached independently of Refresh. A fresh object
                    // is required to detect PID reuse across the property reads.
                    Process verification;
                    try { verification = Process.GetProcessById(process.Id); }
                    catch (ArgumentException) { unavailable++; continue; }
                    using (verification)
                    {
                        var verifiedStart = verification.StartTime.ToUniversalTime().Ticks;
                        if (started <= 0 || started != verifiedStart || cpu < TimeSpan.Zero || workingSet < 0) { unavailable++; continue; }
                        samples.Add(new(process.Id, started, cpu, workingSet));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or UnauthorizedAccessException)
                {
                    unavailable++;
                }
            }
            return new(tracker.Update(samples, clock.GetTimestamp(), maximumInterval), unavailable, processes.Length > MaximumProcesses, exhausted);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }
}

/// <summary>Only calculates CPU for the same PID and start time in consecutive samples.</summary>
public sealed class ProcessRateTracker(TimeProvider clock)
{
    private Dictionary<(int Id, long StartedUtcTicks), TimeSpan> previous = [];
    private long? previousTimestamp;

    public void Reset()
    {
        previous.Clear();
        previousTimestamp = null;
    }

    public IReadOnlyList<ProcessRate> Update(IReadOnlyList<ProcessCounters> current, long timestamp, TimeSpan maximumInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumInterval, TimeSpan.Zero);
        var elapsed = previousTimestamp is { } prior ? clock.GetElapsedTime(prior, timestamp) : TimeSpan.Zero;
        var next = new Dictionary<(int, long), TimeSpan>(current.Count);
        var result = new List<ProcessRate>(current.Count);
        foreach (var item in current)
        {
            var key = (item.Id, item.StartedUtcTicks);
            if (!next.TryAdd(key, item.CpuTime)) throw new ArgumentException("Duplicate process identity.", nameof(current));
            double? percent = null;
            if (elapsed > TimeSpan.Zero && elapsed <= maximumInterval && previous.TryGetValue(key, out var old) && item.CpuTime >= old)
                percent = (item.CpuTime - old).TotalSeconds / elapsed.TotalSeconds * 100;
            result.Add(new(item, percent));
        }
        previous = next;
        previousTimestamp = timestamp;
        return result;
    }
}

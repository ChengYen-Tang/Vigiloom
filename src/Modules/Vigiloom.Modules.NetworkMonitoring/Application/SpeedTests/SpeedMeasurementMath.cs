namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

internal static class SpeedMeasurementMath
{
    // Use batches as samples: discard the first third in chronological order,
    // then retain the fastest 30% of the remaining batches (rounding up).
    // Average retained bytes over retained transfer time rather than averaging rates.
    internal static double CalculateTrimmedRate(IReadOnlyList<(long Bytes, long Milliseconds)> samples)
    {
        var valid = samples.Where(sample => sample.Bytes > 0 && sample.Milliseconds > 0).ToArray();
        if (valid.Length == 0) return 0;
        var recent = valid.Skip(valid.Length / 3).ToArray();
        var count = (int)Math.Ceiling(recent.Length * 0.3d);
        var selected = recent.OrderByDescending(sample => (double)sample.Bytes / sample.Milliseconds).Take(count).ToArray();
        return selected.Sum(sample => (double)sample.Bytes) * 8d / selected.Sum(sample => (double)sample.Milliseconds) / 1000d;
    }

    internal static int NextBatchMiB(int previousMiB, long sampleBytes, long sampleMs, long elapsedMs, long remainingBytes, int targetMilliseconds = SpeedtestNetProbe.DefaultTargetMilliseconds)
    {
        var remainingMiB = Math.Max(0, remainingBytes / (1024d * 1024d));
        // Limit the planned sample to roughly three seconds so a throughput drop
        // cannot leave a single oversized batch running until the phase deadline.
        var remainingTargetMs = Math.Min(3000d, Math.Max(0, targetMilliseconds - elapsedMs));
        var estimateMiB = sampleBytes / (1024d * 1024d) * remainingTargetMs / Math.Max(1, sampleMs);
        return (int)Math.Clamp(Math.Ceiling(estimateMiB), 1, Math.Max(1, Math.Min(remainingMiB, previousMiB * 8d)));
    }

    internal static int NextDownloadConcurrency(int current, int serverCount, long bytes, long milliseconds)
    {
        if (serverCount < 1 || milliseconds <= 0 || bytes <= 0) return current;
        var max = Math.Min(24, serverCount * 6);
        var estimated = bytes / (double)milliseconds * 1000d / 750_000d;
        return Math.Clamp((int)Math.Min(max, Math.Ceiling(estimated)), Math.Min(current, max), max);
    }

}

using System.Globalization;
using System.Runtime.Versioning;

namespace Vigiloom.Agent.Platforms.Linux.Metrics;

/// <summary>Linux CPU time in USER_HZ ticks. Guest time is already included in user and nice.</summary>
public readonly record struct CpuTicks(ulong User, ulong Nice, ulong System, ulong Idle, ulong IoWait, ulong Irq, ulong SoftIrq, ulong Steal)
{
    /// <summary>Non-idle percentage; iowait is treated as idle. Counter regressions produce no result.</summary>
    public double? PercentSince(CpuTicks previous, TimeSpan elapsed, TimeSpan maximumInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumInterval, TimeSpan.Zero);
        if (elapsed <= TimeSpan.Zero || elapsed > maximumInterval || User < previous.User || Nice < previous.Nice ||
            System < previous.System || Idle < previous.Idle || IoWait < previous.IoWait || Irq < previous.Irq ||
            SoftIrq < previous.SoftIrq || Steal < previous.Steal) return null;
        double busy = (double)(User - previous.User) + (Nice - previous.Nice) + (System - previous.System) +
            (Irq - previous.Irq) + (SoftIrq - previous.SoftIrq) + (Steal - previous.Steal);
        double idle = (double)(Idle - previous.Idle) + (IoWait - previous.IoWait);
        return busy + idle > 0 ? busy * 100 / (busy + idle) : null;
    }
}

public static class CpuReader
{
    public const int MaximumCpuLines = 4097; // Aggregate plus at most 4096 logical CPUs.

    [SupportedOSPlatform("linux")]
    public static async Task<IReadOnlyDictionary<string, CpuTicks>?> ReadAsync(CancellationToken token)
    {
        using var reader = new StreamReader("/proc/stat");
        var lines = new List<string>();
        while (await reader.ReadLineAsync(token) is { } line && line.StartsWith("cpu", StringComparison.Ordinal))
        {
            if (lines.Count == MaximumCpuLines) return null;
            lines.Add(line);
        }
        return Parse(lines);
    }

    public static IReadOnlyDictionary<string, CpuTicks>? Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, CpuTicks>(StringComparer.Ordinal);
        Span<ulong> values = stackalloc ulong[8];
        foreach (var line in lines)
        {
            if (result.Count >= MaximumCpuLines) return null;
            var span = line.AsSpan().Trim();
            var split = span.IndexOfAny(' ', '\t');
            if (split < 0) return null;
            var name = span[..split];
            if (!name.SequenceEqual("cpu") && (!name.StartsWith("cpu", StringComparison.Ordinal) || name.Length == 3 ||
                !uint.TryParse(name[3..], NumberStyles.None, CultureInfo.InvariantCulture, out _))) return null;
            span = span[split..].TrimStart();
            for (var i = 0; i < values.Length; i++)
            {
                var end = span.IndexOfAny(' ', '\t');
                var number = end < 0 ? span : span[..end];
                if (!ulong.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) return null;
                span = end < 0 ? ReadOnlySpan<char>.Empty : span[end..].TrimStart();
            }
            if (!result.TryAdd(name.ToString(), new(values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7]))) return null;
        }
        return result.ContainsKey("cpu") ? result : null;
    }
}

using System.Globalization;
using System.Runtime.Versioning;

namespace Vigiloom.Agent.Platforms.Linux.Metrics;

/// <summary>Linux block-device counters. Diskstats sectors are always 512 bytes.</summary>
public readonly record struct DiskCounters(ulong Reads, ulong ReadSectors, ulong ReadMilliseconds,
    ulong Writes, ulong WriteSectors, ulong WriteMilliseconds)
{
    public DiskRates? Since(DiskCounters previous, TimeSpan elapsed, TimeSpan maximumInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumInterval, TimeSpan.Zero);
        if (elapsed <= TimeSpan.Zero || elapsed > maximumInterval || Reads < previous.Reads ||
            ReadSectors < previous.ReadSectors || ReadMilliseconds < previous.ReadMilliseconds ||
            Writes < previous.Writes || WriteSectors < previous.WriteSectors || WriteMilliseconds < previous.WriteMilliseconds)
            return null;
        var reads = Reads - previous.Reads;
        var writes = Writes - previous.Writes;
        return new((ReadSectors - previous.ReadSectors) * 512d / elapsed.TotalSeconds,
            (WriteSectors - previous.WriteSectors) * 512d / elapsed.TotalSeconds,
            reads == 0 ? null : (double)(ReadMilliseconds - previous.ReadMilliseconds) / reads,
            writes == 0 ? null : (double)(WriteMilliseconds - previous.WriteMilliseconds) / writes);
    }
}

public readonly record struct DiskRates(double ReadBytesPerSecond, double WriteBytesPerSecond,
    double? ReadLatencyMilliseconds, double? WriteLatencyMilliseconds);
public sealed record DiskSnapshot(IReadOnlyDictionary<string, DiskCounters> Devices, bool Truncated);

public static class DiskReader
{
    public const int MaximumDevices = 256;
    public const int MaximumLines = 8192;

    [SupportedOSPlatform("linux")]
    public static async Task<DiskSnapshot?> ReadAsync(CancellationToken token)
    {
        using var reader = new StreamReader("/proc/diskstats");
        var lines = new List<string>();
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (lines.Count == MaximumLines) return null;
            lines.Add(line);
        }
        return Parse(lines);
    }

    public static DiskSnapshot? Parse(IEnumerable<string> lines)
    {
        var devices = new Dictionary<string, DiskCounters>(StringComparer.Ordinal);
        var count = 0;
        var truncated = false;
        foreach (var line in lines)
        {
            if (++count > MaximumLines) return null;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 14 || !uint.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
                !uint.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
                string.IsNullOrWhiteSpace(fields[2]) || fields[2].Length > 220 || fields[2].Any(char.IsControl) ||
                !Number(fields[3], out var reads) || !Number(fields[5], out var readSectors) || !Number(fields[6], out var readMs) ||
                !Number(fields[7], out var writes) || !Number(fields[9], out var writeSectors) || !Number(fields[10], out var writeMs)) return null;
            var key = $"{major}:{minor}:{fields[2]}";
            if (devices.Count == MaximumDevices) { truncated = true; continue; }
            if (!devices.TryAdd(key, new(reads, readSectors, readMs, writes, writeSectors, writeMs))) return null;
        }
        return new(devices, truncated);
    }

    private static bool Number(string text, out ulong value) =>
        ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}

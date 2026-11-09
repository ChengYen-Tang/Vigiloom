using System.Globalization;
using System.Runtime.Versioning;

namespace Vigiloom.Agent.Platforms.Linux.Metrics;

/// <summary>Linux physical memory and swap values from the kernel's meminfo interface.</summary>
public readonly record struct MemorySnapshot(ulong TotalBytes, ulong AvailableBytes, ulong? SwapTotalBytes, ulong? SwapFreeBytes);

public static class MemoryReader
{
    [SupportedOSPlatform("linux")]
    public static async Task<MemorySnapshot?> ReadAsync(CancellationToken token) =>
        Parse(await File.ReadAllTextAsync("/proc/meminfo", token));

    /// <summary>Returns no snapshot when required fields are missing or internally inconsistent.</summary>
    public static MemorySnapshot? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ulong? total = null, available = null, swapTotal = null, swapFree = null;
        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var key = line[..separator];
            if (!key.SequenceEqual("MemTotal") && !key.SequenceEqual("MemAvailable") &&
                !key.SequenceEqual("SwapTotal") && !key.SequenceEqual("SwapFree")) continue;
            var value = line[(separator + 1)..].Trim();
            if (!value.EndsWith(" kB", StringComparison.Ordinal) ||
                !ulong.TryParse(value[..^3].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var kib) ||
                kib > ulong.MaxValue / 1024) return null;
            var bytes = kib * 1024;
            if (key.SequenceEqual("MemTotal")) { if (total.HasValue) return null; total = bytes; }
            else if (key.SequenceEqual("MemAvailable")) { if (available.HasValue) return null; available = bytes; }
            else if (key.SequenceEqual("SwapTotal")) { if (swapTotal.HasValue) return null; swapTotal = bytes; }
            else { if (swapFree.HasValue) return null; swapFree = bytes; }
        }
        if (total is not > 0 || available is null || available > total ||
            swapTotal.HasValue != swapFree.HasValue || swapFree > swapTotal) return null;
        return new(total.Value, available.Value, swapTotal, swapFree);
    }
}

using System.Globalization;
using System.Runtime.Versioning;

namespace Vigiloom.Agent.Platforms.Linux.Metrics;

/// <summary>Linux load averages over the previous one, five and fifteen minutes.</summary>
public readonly record struct LoadAverages(double OneMinute, double FiveMinutes, double FifteenMinutes);

public static class LoadReader
{
    [SupportedOSPlatform("linux")]
    public static async Task<LoadAverages?> ReadAsync(CancellationToken token) =>
        Parse(await File.ReadAllTextAsync("/proc/loadavg", token));

    public static LoadAverages? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var remaining = text.AsSpan().TrimStart();
        Span<double> values = stackalloc double[3];
        for (var i = 0; i < values.Length; i++)
        {
            var end = remaining.IndexOfAny(' ', '\t', '\n');
            var number = end < 0 ? remaining : remaining[..end];
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                !double.IsFinite(values[i]) || values[i] < 0) return null;
            remaining = end < 0 ? ReadOnlySpan<char>.Empty : remaining[end..].TrimStart();
        }
        return new(values[0], values[1], values[2]);
    }
}

using OpenTelemetry.Proto.Metrics.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Retains cumulative histograms as snapshots without adding cumulative counts.</summary>
internal static class CumulativeHistogramWindow
{
    internal static DistributionSnapshots.Result<HistogramDataPoint> Reduce(
        IReadOnlyCollection<HistogramDataPoint> points, ulong start, ulong end) =>
        DistributionSnapshots.Reduce(points, start, end, static point => point.TimeUnixNano,
            static point => point.StartTimeUnixNano, static point => point.Flags, static point => point.Count,
            static point => (HistogramValidation.Valid(point, out var unverified), unverified), Compatible);

    private static bool Compatible(HistogramDataPoint previous, HistogramDataPoint current)
    {
        if (previous.BucketCounts.Count == 0 || previous.BucketCounts.Count != current.BucketCounts.Count ||
            !previous.ExplicitBounds.SequenceEqual(current.ExplicitBounds) ||
            previous.HasMin && current.HasMin && current.Min > previous.Min ||
            previous.HasMax && current.HasMax && current.Max < previous.Max) return false;
        for (var i = 0; i < previous.BucketCounts.Count; i++)
            if (current.BucketCounts[i] < previous.BucketCounts[i]) return false;
        return true;
    }
}

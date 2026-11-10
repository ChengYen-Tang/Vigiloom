using OpenTelemetry.Proto.Metrics.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Preserves cumulative summary snapshots; quantiles are never averaged or added.</summary>
internal static class SummaryWindow
{
    internal static DistributionSnapshots.Result<SummaryDataPoint> Reduce(IReadOnlyCollection<SummaryDataPoint> points, ulong start, ulong end) =>
        DistributionSnapshots.Reduce(points, start, end, static point => point.TimeUnixNano,
            static point => point.StartTimeUnixNano, static point => point.Flags, static point => point.Count, Validate);

    private static (bool Valid, bool StatisticsUnverified) Validate(SummaryDataPoint point)
    {
        if (!double.IsFinite(point.Sum) || point.Count == 0 && point.Sum != 0) return (false, false);
        var prior = -1d;
        double? priorValue = null;
        foreach (var quantile in point.QuantileValues)
        {
            if (!double.IsFinite(quantile.Quantile) || quantile.Quantile < 0 || quantile.Quantile > 1 ||
                quantile.Quantile <= prior || !double.IsFinite(quantile.Value) || quantile.Value < 0) return (false, false);
            if (priorValue is { } value && quantile.Value < value) return (false, false);
            prior = quantile.Quantile;
            priorValue = quantile.Value;
        }
        // Quantiles may cover a recent window unrelated to the cumulative count and sum.
        return (true, point.QuantileValues.Count > 0);
    }
}

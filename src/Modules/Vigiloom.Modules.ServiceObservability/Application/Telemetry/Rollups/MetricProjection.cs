using Google.Protobuf;
using OpenTelemetry.Proto.Metrics.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Type-preserving results for one identified metric series and five-minute window.</summary>
/// <remarks>RequiresRaw is a necessary preservation condition, not authorization to delete data.
/// Retention additionally needs a current persisted result and a transactional cleanup fence.</remarks>
internal abstract record MetricProjection(bool RequiresRaw)
{
    internal sealed record Gauge(GaugeWindow.Result Value, bool PreserveRaw) : MetricProjection(PreserveRaw);
    internal sealed record DeltaSum(DeltaSumWindow.Result Value, bool PreserveRaw) : MetricProjection(PreserveRaw);
    internal sealed record CumulativeSum(CumulativeSumWindow.Result Value, bool PreserveRaw) : MetricProjection(PreserveRaw);
    internal sealed record DeltaHistogram(DeltaHistogramWindow.Result Value, bool PreserveRaw) : MetricProjection(PreserveRaw);
    internal sealed record CumulativeHistogram(DistributionSnapshots.Result<HistogramDataPoint> Value, bool PreserveRaw) : MetricProjection(PreserveRaw);
    internal sealed record Summary(DistributionSnapshots.Result<SummaryDataPoint> Value, bool PreserveRaw) : MetricProjection(PreserveRaw);
    internal sealed record Unsupported() : MetricProjection(true);

    // The caller identifies a single series and selects the appropriate endpoint window.
    // A result cannot establish source identity or infer data absent from that input.
    internal static MetricProjection Reduce(Metric metric, IReadOnlyCollection<IMessage> points, MetricWindow window)
    {
        ArgumentNullException.ThrowIfNull(metric);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0 || points.Count > GaugeWindow.MaximumPoints) return new Unsupported();
        var metadataBudget = KnownProtobufFields.MaximumWindowBytes;
        if (!MetricSeriesIdentity.PointFree(metric) ||
            KnownProtobufFields.Inspect(metric, ref metadataBudget) != KnownProtobufFields.Check.Known) return new Unsupported();
        switch (metric.DataCase)
        {
            case Metric.DataOneofCase.Gauge when Typed(points, out NumberDataPoint[] gauge):
            {
                var value = GaugeWindow.Reduce(gauge, window.Start, window.End);
                return new Gauge(value, value.Latest is null || value.Missing > 0 || value.Invalid > 0 ||
                    value.AmbiguousTimestamps || value.UnsupportedFields || value.CapacityExceeded || gauge.Any(x => x.Exemplars.Count > 0));
            }
            case Metric.DataOneofCase.Sum when metric.Sum.AggregationTemporality == AggregationTemporality.Delta &&
                                               Typed(points, out NumberDataPoint[] delta):
            {
                var value = DeltaSumWindow.Reduce(delta, window.Start, window.End, metric.Sum.IsMonotonic);
                return new DeltaSum(value, value.Integer is null && value.Floating is null || value.Missing > 0 ||
                    value.Invalid > 0 || value.Gap || value.Overlap || value.CrossesBoundary || value.Overflow ||
                    value.UnsupportedFields || value.CapacityExceeded || delta.Any(x => x.Exemplars.Count > 0));
            }
            case Metric.DataOneofCase.Sum when metric.Sum.AggregationTemporality == AggregationTemporality.Cumulative &&
                                               Typed(points, out NumberDataPoint[] cumulative):
            {
                var value = CumulativeSumWindow.Reduce(cumulative, window.Start, window.End, metric.Sum.IsMonotonic);
                return new CumulativeSum(value, value.Epochs.Count == 0 || value.Epochs.Any(x => x.First is null || x.Last is null) ||
                    value.Missing > 0 || value.Invalid > 0 || value.UnknownStart || value.AmbiguousTimestamps ||
                    value.OverlappingEpochs || value.Decreased || value.MonotonicityUnknown || value.UnsupportedFields || value.CapacityExceeded ||
                    cumulative.Any(x => x.Exemplars.Count > 0));
            }
            case Metric.DataOneofCase.Histogram when metric.Histogram.AggregationTemporality == AggregationTemporality.Delta &&
                                                     Typed(points, out HistogramDataPoint[] histogram):
            {
                var value = DeltaHistogramWindow.Reduce(histogram, window.Start, window.End);
                return new DeltaHistogram(value, value.Aggregate is null || value.Missing > 0 || value.Invalid > 0 ||
                    value.Gap || value.Overlap || value.CrossesBoundary || value.IncompatibleBounds || value.Overflow ||
                    value.CapacityExceeded || value.ExemplarsOmitted || value.UnsupportedFields);
            }
            case Metric.DataOneofCase.Histogram when metric.Histogram.AggregationTemporality == AggregationTemporality.Cumulative &&
                                                     Typed(points, out HistogramDataPoint[] histogram):
            {
                var value = CumulativeHistogramWindow.Reduce(histogram, window.Start, window.End);
                return new CumulativeHistogram(value, value.Epochs.Count == 0 || value.Epochs.Any(x => x.First is null || x.Last is null) ||
                    value.Missing > 0 || value.Invalid > 0 || value.UnknownStart || value.AmbiguousTimestamps ||
                    value.OverlappingEpochs || value.CountDecreased || value.DistributionIncompatible || value.UnsupportedFields || value.CapacityExceeded ||
                    histogram.Any(x => x.Exemplars.Count > 0));
            }
            case Metric.DataOneofCase.Summary when Typed(points, out SummaryDataPoint[] summary):
            {
                var value = SummaryWindow.Reduce(summary, window.Start, window.End);
                return new Summary(value, value.Epochs.Count == 0 || value.Epochs.Any(x => x.First is null || x.Last is null) ||
                    value.Missing > 0 || value.Invalid > 0 || value.UnknownStart || value.AmbiguousTimestamps ||
                    value.OverlappingEpochs || value.CountDecreased || value.UnsupportedFields || value.CapacityExceeded);
            }
            default:
                // Unsupported kinds/temporality and type mismatches must retain raw inputs.
                return new Unsupported();
        }
    }

    private static bool Typed<T>(IReadOnlyCollection<IMessage> values, out T[] typed) where T : class, IMessage
    {
        typed = values.OfType<T>().ToArray();
        return typed.Length == values.Count;
    }
}

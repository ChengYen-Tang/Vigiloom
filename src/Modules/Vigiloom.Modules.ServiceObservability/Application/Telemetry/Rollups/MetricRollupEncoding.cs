using Google.Protobuf;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Encodes a result as versioned OTLP ResourceMetrics, preserving its type and timestamps.</summary>
internal static class MetricRollupEncoding
{
    internal const int Version = 1;
    private const int MaximumIdentityBytes = 524_288;
    private const int MaximumPayloadBytes = 16 * 1024 * 1024;

    internal static MetricRollupSeries? Encode(MetricWindowGrouping.Series series, MetricWindow window)
    {
        if (series.Identity.Encoded.Length > MaximumIdentityBytes || series.Points.Count == 0) return null;
        var projected = MetricProjection.Reduce(series.Metadata, series.Points, window);
        var metric = series.Metadata.Clone();
        var (mode, quality, hasResult) = Populate(metric, series, projected, window);
        byte[]? payload = null;
        if (hasResult)
        {
            var scope = new ScopeMetrics { Scope = series.Scope, SchemaUrl = series.ScopeSchema };
            scope.Metrics.Add(metric);
            var resource = new ResourceMetrics { Resource = series.Resource, SchemaUrl = series.ResourceSchema };
            resource.ScopeMetrics.Add(scope);
            if (resource.CalculateSize() <= MaximumPayloadBytes) payload = resource.ToByteArray();
            else quality |= MetricRollupQuality.Capacity;
        }
        if (payload is null) quality |= MetricRollupQuality.NoResult;
        return new MetricRollupSeries
        {
            ServiceId = series.ServiceId, InstanceId = series.InstanceId,
            IdentityHash = series.Identity.Fingerprint.ToByteArray(), IdentityBytes = series.Identity.Encoded.ToByteArray(),
            Mode = mode, QualityFlags = (long)quality, RequiresRaw = projected.RequiresRaw || payload is null,
            ResultVersion = Version, InputCount = series.Points.Count, InputBytes = series.InputBytes, ResultPayload = payload
        };
    }

    private static (MetricRollupMode Mode, MetricRollupQuality Quality, bool HasResult) Populate(
        Metric metric, MetricWindowGrouping.Series series, MetricProjection projection, MetricWindow window)
    {
        var quality = MetricRollupQuality.None;
        switch (projection)
        {
            case MetricProjection.Gauge gauge:
                quality |= Common(gauge.Value.Missing, gauge.Value.Invalid, gauge.Value.UnsupportedFields, gauge.Value.CapacityExceeded);
                if (gauge.Value.AmbiguousTimestamps) quality |= MetricRollupQuality.AmbiguousTimestamp;
                if (series.Points.OfType<NumberDataPoint>().Any(x => x.Exemplars.Count > 0)) quality |= MetricRollupQuality.ExemplarsRequireRaw;
                if (gauge.Value.Latest is { } latest) metric.Gauge.DataPoints.Add(latest);
                return (MetricRollupMode.GaugeSnapshot, quality, gauge.Value.Latest is not null);
            case MetricProjection.DeltaSum sum:
                quality |= Common(sum.Value.Missing, sum.Value.Invalid, sum.Value.UnsupportedFields, sum.Value.CapacityExceeded);
                if (sum.Value.Gap) quality |= MetricRollupQuality.Gap;
                if (sum.Value.Overlap) quality |= MetricRollupQuality.Overlap;
                if (sum.Value.CrossesBoundary) quality |= MetricRollupQuality.CrossesBoundary;
                if (sum.Value.Overflow) quality |= MetricRollupQuality.Overflow;
                if (series.Points.OfType<NumberDataPoint>().Any(x => x.Exemplars.Count > 0)) quality |= MetricRollupQuality.ExemplarsRequireRaw;
                if (sum.Value.Integer is null && sum.Value.Floating is null) return (MetricRollupMode.DeltaSum, quality, false);
                var point = new NumberDataPoint { StartTimeUnixNano = window.Start, TimeUnixNano = window.End };
                point.Attributes.Add(((NumberDataPoint)series.Points[0]).Attributes);
                if (sum.Value.Integer is { } integer) point.AsInt = integer;
                else point.AsDouble = sum.Value.Floating!.Value;
                metric.Sum.DataPoints.Add(point);
                return (MetricRollupMode.DeltaSum, quality, true);
            case MetricProjection.CumulativeSum sum:
                quality |= Common(sum.Value.Missing, sum.Value.Invalid, sum.Value.UnsupportedFields, sum.Value.CapacityExceeded);
                if (sum.Value.UnknownStart) quality |= MetricRollupQuality.UnknownStart;
                if (sum.Value.AmbiguousTimestamps) quality |= MetricRollupQuality.AmbiguousTimestamp;
                if (sum.Value.OverlappingEpochs) quality |= MetricRollupQuality.Overlap;
                if (sum.Value.Decreased) quality |= MetricRollupQuality.Decreased;
                if (sum.Value.MonotonicityUnknown) quality |= MetricRollupQuality.MonotonicityUnknown;
                if (series.Points.OfType<NumberDataPoint>().Any(x => x.Exemplars.Count > 0)) quality |= MetricRollupQuality.ExemplarsRequireRaw;
                foreach (var epoch in sum.Value.Epochs) AddSnapshots(metric.Sum.DataPoints, epoch.First, epoch.Last);
                return (MetricRollupMode.CumulativeSumSnapshots, quality, metric.Sum.DataPoints.Count > 0);
            case MetricProjection.DeltaHistogram histogram:
                quality |= Common(histogram.Value.Missing, histogram.Value.Invalid, histogram.Value.UnsupportedFields, histogram.Value.CapacityExceeded);
                if (histogram.Value.Gap) quality |= MetricRollupQuality.Gap;
                if (histogram.Value.Overlap) quality |= MetricRollupQuality.Overlap;
                if (histogram.Value.CrossesBoundary) quality |= MetricRollupQuality.CrossesBoundary;
                if (histogram.Value.IncompatibleBounds) quality |= MetricRollupQuality.Incompatible;
                if (histogram.Value.Overflow) quality |= MetricRollupQuality.Overflow;
                if (histogram.Value.ExemplarsOmitted) quality |= MetricRollupQuality.ExemplarsRequireRaw;
                if (histogram.Value.SumBoundsUnverified) quality |= MetricRollupQuality.StatisticsUnverified;
                if (histogram.Value.Aggregate is { } aggregate) metric.Histogram.DataPoints.Add(aggregate);
                return (MetricRollupMode.DeltaHistogram, quality, histogram.Value.Aggregate is not null);
            case MetricProjection.Summary summary:
                quality |= Common(summary.Value.Missing, summary.Value.Invalid, summary.Value.UnsupportedFields, summary.Value.CapacityExceeded);
                if (summary.Value.UnknownStart) quality |= MetricRollupQuality.UnknownStart;
                if (summary.Value.AmbiguousTimestamps) quality |= MetricRollupQuality.AmbiguousTimestamp;
                if (summary.Value.OverlappingEpochs) quality |= MetricRollupQuality.Overlap;
                if (summary.Value.CountDecreased) quality |= MetricRollupQuality.Decreased;
                if (summary.Value.StatisticsUnverified) quality |= MetricRollupQuality.StatisticsUnverified;
                foreach (var epoch in summary.Value.Epochs) AddSnapshots(metric.Summary.DataPoints, epoch.First, epoch.Last);
                return (MetricRollupMode.SummarySnapshots, quality, metric.Summary.DataPoints.Count > 0);
            default:
                return (MetricRollupMode.Unsupported, MetricRollupQuality.Unsupported, false);
        }
    }

    private static MetricRollupQuality Common(int missing, int invalid, bool unsupported, bool capacity)
    {
        var result = MetricRollupQuality.None;
        if (missing > 0) result |= MetricRollupQuality.Missing;
        if (invalid > 0) result |= MetricRollupQuality.Invalid;
        if (unsupported) result |= MetricRollupQuality.Unsupported;
        if (capacity) result |= MetricRollupQuality.Capacity;
        return result;
    }

    private static void AddSnapshots<T>(Google.Protobuf.Collections.RepeatedField<T> destination, T? first, T? last)
        where T : class, IMessage<T>, IEquatable<T>
    {
        if (first is not null) destination.Add(first);
        if (last is not null && !last.Equals(first)) destination.Add(last);
    }
}

using Google.Protobuf;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>Identifies complete OTLP streams without treating hashes or matching payloads as unique events.</summary>
internal static class MetricWindowGrouping
{
    internal sealed record Series(MetricSeriesIdentity Identity, Resource Resource, InstrumentationScope? Scope,
        string ResourceSchema, string ScopeSchema, Metric Metadata, List<IMessage> Points)
    {
        internal long InputBytes { get; set; }
    }
    internal sealed record Result(IReadOnlyList<Series> Series, bool RequiresRaw);

    internal static Result Group(Guid sourceId, Guid serviceId, MetricWindow window, IReadOnlyList<MetricPoint> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        // Fingerprints bound adversarial dictionary collisions; identity equality still uses
        // the complete encoding, so a real SHA-256 collision cannot merge two streams.
        var groups = new Dictionary<ByteString, List<Series>>();
        var contexts = new Dictionary<MetricContext, ParsedContext>();
        var requiresRaw = false;
        foreach (var row in candidates)
        {
            if (row.ServiceId != serviceId || row.Context.ServiceId != serviceId) { requiresRaw = true; continue; }
            try
            {
                if (!contexts.TryGetValue(row.Context, out var context))
                    contexts.Add(row.Context, context = new ParsedContext(Metric.Parser.ParseFrom(row.Context.MetricPayload)));
                var metric = context.Metadata;
                if (!TryParse(row.Kind, metric, row.PointPayload, out var point, out var attributes, out var timestamp, out var delta))
                { requiresRaw = true; continue; }
                if (!(delta ? MetricWindow.TryForDeltaEnd(timestamp, out var actual) : MetricWindow.TryForSample(timestamp, out actual)))
                { requiresRaw = true; continue; }
                if (actual != window) continue;
                if (context.Resource is null)
                {
                    var parsedResource = Resource.Parser.ParseFrom(row.Context.ResourcePayload);
                    var parsedScope = row.Context.ScopePayload.Length == 0 ? null : InstrumentationScope.Parser.ParseFrom(row.Context.ScopePayload);
                    context.Resource = parsedResource;
                    context.Scope = parsedScope;
                }
                var resource = context.Resource;
                var scope = context.Scope;
                if (!MetricSeriesIdentity.TryCreate(sourceId, serviceId, row.InstanceId, resource, scope,
                        row.Context.ResourceSchemaUrl, row.Context.ScopeSchemaUrl, metric, attributes, out var identity) || identity is null)
                { requiresRaw = true; continue; }
                var bytes = (long)row.PointPayload.Length + row.Context.ResourcePayload.Length + row.Context.ScopePayload.Length +
                    row.Context.MetricPayload.Length + 2L * (row.Context.ResourceSchemaUrl.Length + row.Context.ScopeSchemaUrl.Length);
                if (!groups.TryGetValue(identity.Fingerprint, out var matches))
                    groups.Add(identity.Fingerprint, matches = []);
                var existing = matches.Find(candidate => candidate.Identity.SameSeries(identity));
                if (existing is not null)
                {
                    existing.Points.Add(point);
                    existing.InputBytes = checked(existing.InputBytes + bytes);
                }
                else matches.Add(new(identity, resource, scope, row.Context.ResourceSchemaUrl,
                    row.Context.ScopeSchemaUrl, metric, [point]) { InputBytes = bytes });
            }
            catch (InvalidProtocolBufferException) { requiresRaw = true; }
        }
        return new(groups.Values.SelectMany(group => group).ToArray(), requiresRaw);
    }

    private sealed class ParsedContext(Metric metadata)
    {
        internal Metric Metadata { get; } = metadata;
        internal Resource? Resource { get; set; }
        internal InstrumentationScope? Scope { get; set; }
    }

    private static bool TryParse(byte kind, Metric metadata, byte[] payload, out IMessage point,
        out RepeatedField<KeyValue> attributes, out ulong timestamp, out bool delta)
    {
        delta = false;
        switch (kind)
        {
            case 1 when metadata.DataCase == Metric.DataOneofCase.Gauge:
            case 2 when metadata.DataCase == Metric.DataOneofCase.Sum:
                var number = NumberDataPoint.Parser.ParseFrom(payload);
                point = number; attributes = number.Attributes; timestamp = number.TimeUnixNano;
                delta = kind == 2 && metadata.Sum.AggregationTemporality == AggregationTemporality.Delta;
                return true;
            case 3 when metadata.DataCase == Metric.DataOneofCase.Histogram:
                var histogram = HistogramDataPoint.Parser.ParseFrom(payload);
                point = histogram; attributes = histogram.Attributes; timestamp = histogram.TimeUnixNano;
                delta = metadata.Histogram.AggregationTemporality == AggregationTemporality.Delta;
                return true;
            case 4 when metadata.DataCase == Metric.DataOneofCase.ExponentialHistogram:
                var exponential = ExponentialHistogramDataPoint.Parser.ParseFrom(payload);
                point = exponential; attributes = exponential.Attributes; timestamp = exponential.TimeUnixNano;
                delta = metadata.ExponentialHistogram.AggregationTemporality == AggregationTemporality.Delta;
                return true;
            case 5 when metadata.DataCase == Metric.DataOneofCase.Summary:
                var summary = SummaryDataPoint.Parser.ParseFrom(payload);
                point = summary; attributes = summary.Attributes; timestamp = summary.TimeUnixNano;
                return true;
            default:
                point = null!; attributes = null!; timestamp = 0; return false;
        }
    }
}

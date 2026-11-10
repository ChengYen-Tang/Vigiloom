using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OpenTelemetry.Proto.Resource.V1;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

/// <summary>A canonical, collision-checkable identity for an authenticated OTLP metric stream.</summary>
internal sealed class MetricSeriesIdentity
{
    private const int MaximumDepth = 32;
    private MetricSeriesIdentity(byte[] encoded)
    {
        Encoded = ByteString.CopyFrom(encoded);
        Fingerprint = ByteString.CopyFrom(SHA256.HashData(encoded));
    }

    internal ByteString Fingerprint { get; }
    internal ByteString Encoded { get; }

    // Never use the fingerprint as a substitute for comparing the complete encoded identity.
    internal bool SameSeries(MetricSeriesIdentity other) => Encoded.Equals(other.Encoded);

    internal static bool TryCreate(Guid source, Guid service, Guid? instance, Resource resource,
        InstrumentationScope? scope, string resourceSchema, string scopeSchema, Metric metadata,
        IEnumerable<KeyValue> pointAttributes, out MetricSeriesIdentity? identity)
    {
        identity = null;
        if (source.Version != 7 || service.Version != 7 || instance is { Version: not 7 } ||
            resource is null || metadata is null || pointAttributes is null || resourceSchema is null || scopeSchema is null)
            return false;
        var inputAttributes = pointAttributes.Take(4097).ToArray();
        var attributeBudget = 16_384;
        if (inputAttributes.Length > 4096 || resource.DroppedAttributesCount != 0 || scope?.DroppedAttributesCount > 0 ||
            !ValidAttributes(resource.Attributes, 0, ref attributeBudget) || scope is not null && !ValidAttributes(scope.Attributes, 0, ref attributeBudget) ||
            !ValidAttributes(metadata.Metadata, 0, ref attributeBudget) || !ValidAttributes(inputAttributes, 0, ref attributeBudget) || resource.CalculateSize() > 65_536 ||
            (scope?.CalculateSize() ?? 0) > 65_536 || inputAttributes.Sum(item => (long)item.CalculateSize()) > 262_144 ||
            !PointFree(metadata) || metadata.CalculateSize() > 65_536 || resourceSchema.Length > 2048 || scopeSchema.Length > 2048 ||
            metadata.Name.Length == 0) return false;
        var remainingBytes = KnownProtobufFields.MaximumWindowBytes;
        var sourceAttributes = new KeyValueList();
        sourceAttributes.Values.Add(inputAttributes);
        if (KnownProtobufFields.Inspect(resource, ref remainingBytes) != KnownProtobufFields.Check.Known ||
            scope is not null && KnownProtobufFields.Inspect(scope, ref remainingBytes) != KnownProtobufFields.Check.Known ||
            KnownProtobufFields.Inspect(metadata, ref remainingBytes) != KnownProtobufFields.Check.Known ||
            KnownProtobufFields.Inspect(sourceAttributes, ref remainingBytes) != KnownProtobufFields.Check.Known) return false;
        var canonicalResource = resource.Clone();
        var canonicalScope = scope?.Clone() ?? new InstrumentationScope();
        var canonicalMetric = metadata.Clone();
        canonicalMetric.Description = "";
        canonicalMetric.Metadata.Clear();
        var canonicalAttributes = new RepeatedField<KeyValue>();
        canonicalAttributes.Add(inputAttributes.Select(attribute => attribute.Clone()));
        if (!Normalize(canonicalResource.Attributes, 0) ||
            !Normalize(canonicalScope.Attributes, 0) ||
            !Normalize(canonicalAttributes, 0)) return false;

        // Descriptors are framed to prevent ambiguity between adjacent fields. The metric
        // is the point-free metadata saved by ingestion, including kind and temporality.
        using var output = new MemoryStream();
        using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((byte)1);
            writer.Write(source.ToByteArray());
            writer.Write(service.ToByteArray());
            writer.Write(instance.HasValue);
            if (instance is { } id) writer.Write(id.ToByteArray());
            Write(writer, canonicalResource.ToByteArray());
            Write(writer, canonicalScope.ToByteArray());
            writer.Write(resourceSchema);
            writer.Write(scopeSchema);
            Write(writer, canonicalMetric.ToByteArray());
            var attributes = new KeyValueList();
            attributes.Values.Add(canonicalAttributes);
            Write(writer, attributes.ToByteArray());
        }
        identity = new(output.ToArray());
        return true;
    }

    private static bool PointFree(Metric metric) => metric.DataCase switch
    {
        Metric.DataOneofCase.Gauge => metric.Gauge.DataPoints.Count == 0,
        Metric.DataOneofCase.Sum => metric.Sum.DataPoints.Count == 0 && ValidTemporality(metric.Sum.AggregationTemporality),
        Metric.DataOneofCase.Histogram => metric.Histogram.DataPoints.Count == 0 && ValidTemporality(metric.Histogram.AggregationTemporality),
        Metric.DataOneofCase.ExponentialHistogram => metric.ExponentialHistogram.DataPoints.Count == 0 && ValidTemporality(metric.ExponentialHistogram.AggregationTemporality),
        Metric.DataOneofCase.Summary => metric.Summary.DataPoints.Count == 0,
        _ => false
    };

    private static bool ValidTemporality(AggregationTemporality value) =>
        value is AggregationTemporality.Delta or AggregationTemporality.Cumulative;

    private static void Write(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static bool ValidAttributes(IEnumerable<KeyValue> attributes, int depth, ref int budget)
    {
        if (depth > MaximumDepth) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in attributes)
        {
            if (--budget < 0 || item is null || item.KeyStrindex != 0 || string.IsNullOrEmpty(item.Key) ||
                !keys.Add(item.Key) || item.Value is null || !ValidValue(item.Value, depth + 1, ref budget)) return false;
        }
        return true;
    }

    private static bool ValidValue(AnyValue value, int depth, ref int budget)
    {
        if (depth > MaximumDepth || value.ValueCase is AnyValue.ValueOneofCase.None or AnyValue.ValueOneofCase.StringValueStrindex)
            return false;
        if (value.ValueCase == AnyValue.ValueOneofCase.KvlistValue)
            return value.KvlistValue is not null && ValidAttributes(value.KvlistValue.Values, depth + 1, ref budget);
        if (value.ValueCase == AnyValue.ValueOneofCase.ArrayValue)
        {
            if (value.ArrayValue is null) return false;
            foreach (var item in value.ArrayValue.Values)
                if (--budget < 0 || item is null || !ValidValue(item, depth + 1, ref budget)) return false;
        }
        return true;
    }

    private static bool Normalize(RepeatedField<KeyValue> attributes, int depth)
    {
        if (depth > MaximumDepth) return false;
        var ordered = attributes.OrderBy(attribute => attribute.Key, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var item = ordered[index];
            if (item.KeyStrindex != 0 || string.IsNullOrEmpty(item.Key) || item.Value is null ||
                index > 0 && string.Equals(ordered[index - 1].Key, item.Key, StringComparison.Ordinal) ||
                !NormalizeValue(item.Value, depth + 1)) return false;
        }
        attributes.Clear();
        attributes.Add(ordered);
        return true;
    }

    private static bool NormalizeValue(AnyValue value, int depth)
    {
        if (depth > MaximumDepth || value.ValueCase is AnyValue.ValueOneofCase.None or AnyValue.ValueOneofCase.StringValueStrindex)
            return false;
        if (value.ValueCase == AnyValue.ValueOneofCase.KvlistValue)
            return value.KvlistValue is not null && Normalize(value.KvlistValue.Values, depth + 1);
        if (value.ValueCase == AnyValue.ValueOneofCase.ArrayValue)
            return value.ArrayValue is not null && value.ArrayValue.Values.All(item => NormalizeValue(item, depth + 1));
        return true;
    }
}

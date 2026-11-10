using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

/// <summary>Host metrics use a separate wire version so existing runtime batches remain unchanged.</summary>
public sealed record AgentMetricBatch(int SchemaVersion, Guid BatchId, DateTimeOffset EnqueuedAt, IReadOnlyList<AgentMetricPoint> Items);

/// <summary>Each point is observed at the Agent; missing values are omitted rather than encoded as zero.</summary>
public sealed record AgentMetricPoint(Guid MessageId, DateTimeOffset OccurredAt, AgentMetricKind Kind,
    string Source, string? Dimension, [property: JsonRequired] double Value);

/// <summary>Units are encoded in the names: bytes, bytes/second, errors/second, or percent.</summary>
public enum AgentMetricKind
{
    CpuTotalPercent = 1, CpuCorePercent = 2, LoadAverage1 = 3, LoadAverage5 = 4, LoadAverage15 = 5,
    MemoryTotalBytes = 10, MemoryAvailableBytes = 11, SwapTotalBytes = 12, SwapFreeBytes = 13,
    PageFileTotalBytes = 14, PageFileUsedBytes = 15,
    VolumeTotalBytes = 20, VolumeAvailableBytes = 21,
    DiskReadBytesPerSecond = 22, DiskWriteBytesPerSecond = 23, DiskReadLatencyMilliseconds = 24, DiskWriteLatencyMilliseconds = 25,
    NetworkReceivedBytesPerSecond = 30, NetworkSentBytesPerSecond = 31,
    NetworkReceivedErrorsPerSecond = 32, NetworkSentErrorsPerSecond = 33,
    ProcessCpuPercent = 40, ProcessWorkingSetBytes = 41,
    CollectionUnavailable = 50
}

/// <summary>Validates immutable, bounded metric batches before they enter the durable queue or database.</summary>
public static class AgentMetricBatchCodec
{
    public const int SchemaVersion = 2;
    public const string SupportedSchemaHeader = "Vigiloom-Agent-Batch-Schemas";
    // Values are nonnegative. This generous ceiling is well beyond plausible host
    // readings and keeps SQL float SUM/AVG finite even across a bigint row count.
    public const double MaximumValue = 1e30;
    private static readonly JsonSerializerOptions Options = AgentBatchJson.CreateOptions<AgentMetricPoint>();

    public static byte[] Serialize(AgentMetricBatch batch)
    {
        if (!IsValid(batch)) throw new ArgumentException("Invalid Agent metric batch.", nameof(batch));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(batch, Options);
        return bytes.Length <= AgentBatchCodec.MaximumBytes ? bytes : throw new ArgumentException("Agent metric batch is too large.", nameof(batch));
    }

    public static bool TryRead(ReadOnlySpan<byte> bytes, out AgentMetricBatch? batch)
    {
        batch = null;
        if (bytes.Length is < 1 or > AgentBatchCodec.MaximumBytes) return false;
        try
        {
            var decoded = JsonSerializer.Deserialize<AgentMetricBatch>(bytes, Options);
            if (!IsValid(decoded)) return false;
            batch = decoded;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsValid(AgentMetricBatch? batch)
    {
        if (batch is null || batch.SchemaVersion != SchemaVersion || batch.BatchId.Version != 7 ||
            batch.EnqueuedAt == default || batch.EnqueuedAt.Offset != TimeSpan.Zero ||
            batch.Items is not { Count: > 0 and <= AgentBatchCodec.MaximumItems }) return false;
        var ids = new HashSet<Guid>();
        foreach (var point in batch.Items)
        {
            if (point is null || point.MessageId.Version != 7 || !ids.Add(point.MessageId) ||
                point.OccurredAt == default || point.OccurredAt.Offset != TimeSpan.Zero ||
                !Enum.IsDefined(point.Kind) || !ValidText(point.Source, 128) ||
                (RequiresDimension(point.Kind) ? !ValidText(point.Dimension, 256) : point.Dimension is not null) ||
                !double.IsFinite(point.Value) || point.Value < 0 || point.Value > MaximumValue ||
                (point.Kind == AgentMetricKind.CollectionUnavailable && point.Value != 1) ||
                (point.Kind is AgentMetricKind.CpuTotalPercent or AgentMetricKind.CpuCorePercent && point.Value > 100)) return false;
        }
        return true;
    }

    private static bool ValidText(string? value, int limit) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Any(char.IsControl);

    private static bool RequiresDimension(AgentMetricKind kind) => kind is AgentMetricKind.CpuCorePercent or
        AgentMetricKind.VolumeTotalBytes or AgentMetricKind.VolumeAvailableBytes or
        AgentMetricKind.DiskReadBytesPerSecond or AgentMetricKind.DiskWriteBytesPerSecond or
        AgentMetricKind.DiskReadLatencyMilliseconds or AgentMetricKind.DiskWriteLatencyMilliseconds or
        AgentMetricKind.NetworkReceivedBytesPerSecond or AgentMetricKind.NetworkSentBytesPerSecond or
        AgentMetricKind.NetworkReceivedErrorsPerSecond or AgentMetricKind.NetworkSentErrorsPerSecond or
        AgentMetricKind.ProcessCpuPercent or AgentMetricKind.ProcessWorkingSetBytes or AgentMetricKind.CollectionUnavailable;

}

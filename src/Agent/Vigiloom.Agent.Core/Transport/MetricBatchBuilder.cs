using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Transport;

/// <summary>Creates immutable bounded wire batches from an ordered metric collection.</summary>
public static class MetricBatchBuilder
{
    // A point can contain up to 384 UTF-16 characters of source and dimension.
    // JSON escaping can take six bytes per character; 250 leaves room for the
    // envelope and the remaining fields within the one-MiB protocol limit.
    public const int MaximumPointsPerBatch = 250;

    public static IReadOnlyList<QueuedBatch> Create(IReadOnlyList<AgentMetricPoint> points, DateTimeOffset enqueuedAt)
    {
        ArgumentNullException.ThrowIfNull(points);
        var batches = new List<QueuedBatch>((points.Count + MaximumPointsPerBatch - 1) / MaximumPointsPerBatch);
        for (var offset = 0; offset < points.Count; offset += MaximumPointsPerBatch)
        {
            var count = Math.Min(MaximumPointsPerBatch, points.Count - offset);
            var items = new AgentMetricPoint[count];
            for (var i = 0; i < count; i++) items[i] = points[offset + i];
            var batch = new AgentMetricBatch(AgentMetricBatchCodec.SchemaVersion, Guid.CreateVersion7(), enqueuedAt, items);
            var payload = AgentMetricBatchCodec.Serialize(batch);
            batches.Add(new(batch.BatchId, batch.EnqueuedAt, count, payload));
        }
        return batches;
    }
}

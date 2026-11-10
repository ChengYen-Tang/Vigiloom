using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Transport;

/// <summary>Maintains one immutable pending sample across queue backpressure and transient failures. Use from a single producer.</summary>
public sealed class MetricSamplePublisher(DurableBatchQueue queue, AgentSettingsOwner owner)
{
    private Queue<QueuedBatch>? pending;
    private bool outcomeUnknown;
    public bool HasPending => pending is not null;
    public int PendingBatches => pending?.Count ?? 0;
    public long PendingRecords => pending?.Sum(batch => (long)batch.RecordCount) ?? 0;
    /// <summary>Whether a pending batch might already have committed despite an interrupted enqueue.</summary>
    public bool PendingOutcomeUnknown => outcomeUnknown;
    public int KnownUnqueuedBatches => PendingBatches - (outcomeUnknown && HasPending ? 1 : 0);
    public long KnownUnqueuedRecords => PendingRecords - (outcomeUnknown && pending is not null && pending.TryPeek(out var first) ? first.RecordCount : 0);

    /// <summary>Prepare only when the previous sample has been durably queued.</summary>
    public void Prepare(IReadOnlyList<AgentMetricPoint> points, DateTimeOffset enqueuedAt)
    {
        if (pending is not null) throw new InvalidOperationException("A metric sample is already pending.");
        var result = MetricBatchBuilder.Create(points, enqueuedAt);
        if (result.Count == 0) throw new ArgumentException("A sample must contain at least one point.", nameof(points));
        pending = new Queue<QueuedBatch>(result);
    }

    /// <summary>Persist batches in order, retaining unchanged bytes and IDs for the first batch that cannot be queued.</summary>
    public async Task<EnqueueResult> FlushAsync(CancellationToken token)
    {
        var sample = pending ?? throw new InvalidOperationException("No metric sample is pending.");
        while (sample.TryPeek(out var batch))
        {
            var previouslyUnknown = outcomeUnknown;
            // An exception can occur after commit, so retain uncertainty until
            // this particular batch is confirmed queued or already queued.
            outcomeUnknown = true;
            var result = await queue.EnqueueManyForOwnerAsync([batch], owner, token);
            if (result is not (EnqueueResult.Queued or EnqueueResult.AlreadyQueued))
            {
                outcomeUnknown = previouslyUnknown;
                return result;
            }
            sample.Dequeue();
            outcomeUnknown = false;
        }
        pending = null;
        return EnqueueResult.Queued;
    }
}

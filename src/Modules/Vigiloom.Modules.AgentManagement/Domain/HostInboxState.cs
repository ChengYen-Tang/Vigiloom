using Vigiloom.Contracts.Devices;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>Host-owned receipt state for an Agent whose identity is managed by Resources.</summary>
public sealed class HostInboxState
{
    public Guid Id { get; set; }
    public long RowId { get; private set; }
    public Guid DeviceId { get; set; }
    public long DeviceGeneration { get; set; }
    public DateTimeOffset? ReplayClockFloor { get; set; }
    public DateTimeOffset? ReplayCleanedBefore { get; set; }
    public DateTimeOffset? LastBatchReceivedAt { get; set; }
    public DateTimeOffset? LastBatchEnqueuedAt { get; set; }
    public long? LastBatchReceiptRowId { get; set; }
    public long InboxDeletedTotal { get; set; }
    public int InboxLastDeletedCount { get; set; }
    public DateTimeOffset? InboxLastDeletedAt { get; set; }
    public long? InboxReceiptCount { get; set; }
    public long? InboxReceiptPayloadBytes { get; set; }
    public AgentReplayIssue? LastReplayIssue { get; set; }
    public DateTimeOffset? LastReplayIssueAt { get; set; }

    public void RecordCommittedBatch(ReceivedBatch receipt)
    {
        if (LastBatchReceivedAt is { } last && (receipt.ReceivedAt < last ||
            receipt.ReceivedAt == last && receipt.RowId <= (LastBatchReceiptRowId ?? 0))) return;
        LastBatchReceivedAt = receipt.ReceivedAt;
        LastBatchEnqueuedAt = receipt.EnqueuedAt;
        LastBatchReceiptRowId = receipt.RowId;
    }
}

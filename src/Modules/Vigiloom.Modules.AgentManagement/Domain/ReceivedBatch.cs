namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>High-volume receipt; the protocol batch ID is scoped to the authenticated Agent.</summary>
public sealed class ReceivedBatch
{
    private ReceivedBatch() { }
    public ReceivedBatch(Guid agentId, Guid batchId, byte[] digest, DateTimeOffset enqueuedAt, DateTimeOffset receivedAt, int itemCount, int byteCount,
        DateTimeOffset? trustedReceivedAt = null)
    {
        AgentId = agentId; BatchId = batchId; Digest = digest; EnqueuedAt = enqueuedAt.ToUniversalTime();
        ReceivedAt = receivedAt.ToUniversalTime(); TrustedReceivedAt = trustedReceivedAt?.ToUniversalTime(); ItemCount = itemCount; ByteCount = byteCount;
    }
    public long RowId { get; private set; }
    public Guid AgentId { get; private set; }
    public Guid BatchId { get; private set; }
    public byte[] Digest { get; private set; } = [];
    public DateTimeOffset EnqueuedAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    /// <summary>SQL clock at acceptance; null for receipts created before this field was introduced.</summary>
    public DateTimeOffset? TrustedReceivedAt { get; private set; }
    public int ItemCount { get; private set; }
    public int ByteCount { get; private set; }
}

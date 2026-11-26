using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;
using System.Text.Json;

namespace Vigiloom.Modules.AgentManagement.Domain;

public sealed class Agent : AgentStatus
{
    private Agent() { }

    public Agent(Guid deviceId, long generation, byte[] verifier, DateTimeOffset now)
    {
        Id = ResourceIds.Create();
        DeviceId = deviceId;
        DeviceGeneration = generation;
        Verifier = verifier;
        CreatedAt = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid DeviceId { get; private set; }
    public long DeviceGeneration { get; private set; }
    public byte[] Verifier { get; private set; } = [];
    public byte[]? PendingVerifier { get; private set; }
    public DateTimeOffset? PendingUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public DateTimeOffset? ReplayClockFloor { get; private set; }
    /// <summary>Exclusive enqueue-time lower bound after receipt cleanup; null before cleanup.</summary>
    public DateTimeOffset? ReplayCleanedBefore { get; private set; }
    public DateTimeOffset? LastBatchReceivedAt { get; private set; }
    public DateTimeOffset? LastBatchEnqueuedAt { get; private set; }
    public long? LastBatchReceiptRowId { get; private set; }
    public long InboxDeletedTotal { get; private set; }
    public int InboxLastDeletedCount { get; private set; }
    public DateTimeOffset? InboxLastDeletedAt { get; private set; }
    public long? InboxReceiptCount { get; private set; }
    public long? InboxReceiptPayloadBytes { get; private set; }
    public AgentReplayIssue? LastReplayIssue { get; private set; }
    public DateTimeOffset? LastReplayIssueAt { get; private set; }
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now.ToUniversalTime();
    public void AdvanceReplayClock(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        if (ReplayClockFloor is null || utc > ReplayClockFloor) ReplayClockFloor = utc;
    }
    public void AdvanceCleanupFence(DateTimeOffset boundary)
    {
        var utc = boundary.ToUniversalTime();
        if (ReplayCleanedBefore is null || utc > ReplayCleanedBefore) ReplayCleanedBefore = utc;
    }
    public void RecordInboxCleanup(int deleted, DateTimeOffset now)
    {
        if (deleted <= 0) throw new ArgumentOutOfRangeException(nameof(deleted));
        InboxDeletedTotal = checked(InboxDeletedTotal + deleted);
        InboxLastDeletedCount = deleted;
        InboxLastDeletedAt = now.ToUniversalTime();
    }
    public void InitializeInboxUsage(long count, long bytes)
    {
        if (InboxReceiptCount is not null || InboxReceiptPayloadBytes is not null || count < 0 || bytes < 0)
            throw new InvalidOperationException("Inbox usage cannot be initialized.");
        InboxReceiptCount = count;
        InboxReceiptPayloadBytes = bytes;
    }
    public void ChangeInboxUsage(long countDelta, long bytesDelta)
    {
        if (InboxReceiptCount is not { } count || InboxReceiptPayloadBytes is not { } bytes)
            throw new InvalidOperationException("Inbox usage is not initialized.");
        var nextCount = checked(count + countDelta);
        var nextBytes = checked(bytes + bytesDelta);
        if (nextCount < 0 || nextBytes < 0) throw new InvalidOperationException("Inbox usage cannot be negative.");
        InboxReceiptCount = nextCount;
        InboxReceiptPayloadBytes = nextBytes;
    }
    public void RecordCommittedBatch(DateTimeOffset enqueuedAt, DateTimeOffset receivedAt, long receiptRowId)
    {
        var utc = receivedAt.ToUniversalTime();
        if (LastBatchReceivedAt is { } last && (utc < last || utc == last && receiptRowId <= (LastBatchReceiptRowId ?? 0))) return;
        LastBatchReceivedAt = utc;
        LastBatchEnqueuedAt = enqueuedAt.ToUniversalTime();
        LastBatchReceiptRowId = receiptRowId;
    }
    public void RecordReplayIssue(AgentReplayIssue issue, DateTimeOffset now)
    {
        if (!Enum.IsDefined(issue)) throw new ArgumentOutOfRangeException(nameof(issue));
        LastReplayIssue = issue;
        LastReplayIssueAt = now.ToUniversalTime();
    }
    public void ClearReplayIssue() { LastReplayIssue = null; LastReplayIssueAt = null; }
    public void BeginRotation(byte[] verifier, DateTimeOffset until) { PendingVerifier = verifier; PendingUntil = until.ToUniversalTime(); }
    public void FinishRotation() { Verifier = PendingVerifier!; PendingVerifier = null; PendingUntil = null; }
}

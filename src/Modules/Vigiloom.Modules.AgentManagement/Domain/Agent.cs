using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Devices;

namespace Vigiloom.Modules.AgentManagement.Domain;

public sealed class Agent
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
    public DateTimeOffset? LastHeartbeatAt { get; private set; }
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
    public int HeartbeatSafetyIntervalSeconds { get; private set; } = 30;
    public DateTimeOffset? QueueReportedAt { get; private set; }
    public Guid? QueueReportEpoch { get; private set; }
    public long? QueueReportSequence { get; private set; }
    public long? QueueTotalBytes { get; private set; }
    public long? QueueTotalBatches { get; private set; }
    public long? QueueQuarantinedBatches { get; private set; }
    public long? QueueCapacityBytes { get; private set; }
    public int? QueueCapacityBatches { get; private set; }
    public long? QuarantineInvalidLocal { get; private set; }
    public long? QuarantineBadRequest { get; private set; }
    public long? QuarantineConflict { get; private set; }
    public long? QuarantineTooLarge { get; private set; }
    public long? QuarantineUnsupported { get; private set; }
    public long? QuarantineExpired { get; private set; }
    public long? QuarantineOther { get; private set; }
    public Guid? QuarantineReportEpoch { get; private set; }
    public long? QuarantineReportSequence { get; private set; }
    public Guid? QueueCapacityReportEpoch { get; private set; }
    public long? QueueCapacityReportSequence { get; private set; }
    public DateTimeOffset? QueueCapacityReportedAt { get; private set; }
    public string? QueueCapacityAppliedRevision { get; private set; }
    public long? QueueCapacityAppliedBytes { get; private set; }
    public int? QueueCapacityAppliedBatches { get; private set; }
    public DateTimeOffset? QueueCapacityAppliedAt { get; private set; }
    public AgentConfigurationFailure? QueueCapacityFailure { get; private set; }
    public DateTimeOffset? ConfigurationReportedAt { get; private set; }
    public string? AppliedConfigurationRevision { get; private set; }
    public int? AppliedHeartbeatSeconds { get; private set; }
    public int? AppliedSettingsPollSeconds { get; private set; }
    public int? AppliedBatchIntervalSeconds { get; private set; }
    public DateTimeOffset? ConfigurationAppliedAt { get; private set; }
    public AgentConfigurationFailure? ConfigurationFailure { get; private set; }
    public Guid? MetricLossReportEpoch { get; private set; }
    public long? MetricLossReportSequence { get; private set; }
    public DateTimeOffset? MetricLossReportedAt { get; private set; }
    public Guid? MetricLossLedgerId { get; private set; }
    public DateTimeOffset? MetricLossStartedAt { get; private set; }
    public long? MetricLossBatches { get; private set; }
    public long? MetricLossRecords { get; private set; }
    public bool? MetricLossCompletenessUnknown { get; private set; }

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
    public void RecordHeartbeat(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        if (LastHeartbeatAt is null || utc > LastHeartbeatAt) LastHeartbeatAt = utc;
    }
    public void WidenHeartbeatSafety(int intervalSeconds)
    {
        if (intervalSeconds is < 10 or > 300) throw new ArgumentOutOfRangeException(nameof(intervalSeconds));
        HeartbeatSafetyIntervalSeconds = Math.Max(HeartbeatSafetyIntervalSeconds, intervalSeconds);
    }
    public bool RecordQueueStatus(long sequence, AgentQueueSnapshot? snapshot, AgentQuarantineBreakdown? quarantine, DateTimeOffset now)
    {
        if (QueueReportSequence >= sequence) return false;
        QueueReportSequence = sequence;
        QueueReportedAt = now.ToUniversalTime();
        QueueTotalBytes = snapshot?.TotalBytes;
        QueueTotalBatches = snapshot?.TotalBatches;
        QueueQuarantinedBatches = snapshot?.QuarantinedBatches;
        QueueCapacityBytes = snapshot?.CapacityBytes;
        QueueCapacityBatches = snapshot?.CapacityBatches;
        QuarantineInvalidLocal = quarantine?.InvalidLocalBatch;
        QuarantineBadRequest = quarantine?.BadRequest;
        QuarantineConflict = quarantine?.Conflict;
        QuarantineTooLarge = quarantine?.PayloadTooLarge;
        QuarantineUnsupported = quarantine?.UnsupportedMediaType;
        QuarantineExpired = quarantine?.Expired;
        QuarantineOther = quarantine?.Other;
        QuarantineReportEpoch = quarantine is null ? null : QueueReportEpoch;
        QuarantineReportSequence = quarantine is null ? null : sequence;
        return true;
    }
    public void RecordConfigurationStatus(AgentConfigurationReport report, DateTimeOffset now)
    {
        ConfigurationReportedAt = now.ToUniversalTime();
        AppliedConfigurationRevision = report.Applied?.Revision;
        AppliedHeartbeatSeconds = report.Applied?.Settings.HeartbeatSeconds;
        AppliedSettingsPollSeconds = report.Applied?.Settings.SettingsPollSeconds;
        AppliedBatchIntervalSeconds = report.Applied?.Settings.BatchIntervalSeconds;
        ConfigurationAppliedAt = report.AppliedAt?.ToUniversalTime();
        ConfigurationFailure = report.Failure;
    }

    public void RecordMetricLossStatus(AgentMetricLossReport? report, long sequence, DateTimeOffset now)
    {
        // A missing or contradictory snapshot must not refresh stale values.
        if (report is not null && MetricLossLedgerId == report.LedgerId &&
            (MetricLossStartedAt != report.StartedAt || MetricLossBatches > report.Batches || MetricLossRecords > report.Records ||
             MetricLossCompletenessUnknown == true && !report.CompletenessUnknown)) report = null;
        MetricLossReportEpoch = report is null ? null : QueueReportEpoch;
        MetricLossReportSequence = report is null ? null : sequence;
        MetricLossReportedAt = report is null ? null : now.ToUniversalTime();
        if (report is null) return;
        MetricLossLedgerId = report.LedgerId;
        MetricLossStartedAt = report.StartedAt;
        MetricLossBatches = report.Batches;
        MetricLossRecords = report.Records;
        MetricLossCompletenessUnknown = report.CompletenessUnknown;
    }
    public void RecordQueueCapacityStatus(AgentQueueCapacityReport report, long sequence, DateTimeOffset now)
    {
        QueueCapacityReportEpoch = QueueReportEpoch;
        QueueCapacityReportSequence = sequence;
        QueueCapacityReportedAt = now.ToUniversalTime();
        QueueCapacityAppliedRevision = report.Applied?.Revision;
        QueueCapacityAppliedBytes = report.Applied?.Capacity.PayloadBytes;
        QueueCapacityAppliedBatches = report.Applied?.Capacity.BatchCount;
        QueueCapacityAppliedAt = report.AppliedAt?.ToUniversalTime();
        QueueCapacityFailure = report.Failure;
    }
    public void BeginQueueSession(Guid epoch)
    {
        QueueReportEpoch = epoch;
        QueueReportSequence = null;
        QueueReportedAt = null;
        QueueTotalBytes = null;
        QueueTotalBatches = null;
        QueueQuarantinedBatches = null;
        QueueCapacityBytes = null;
        QueueCapacityBatches = null;
        QuarantineInvalidLocal = null;
        QuarantineBadRequest = null;
        QuarantineConflict = null;
        QuarantineTooLarge = null;
        QuarantineUnsupported = null;
        QuarantineExpired = null;
        QuarantineOther = null;
        QuarantineReportEpoch = null;
        QuarantineReportSequence = null;
        QueueCapacityReportEpoch = null;
        QueueCapacityReportSequence = null;
        QueueCapacityReportedAt = null;
        QueueCapacityAppliedRevision = null;
        QueueCapacityAppliedBytes = null;
        QueueCapacityAppliedBatches = null;
        QueueCapacityAppliedAt = null;
        QueueCapacityFailure = null;
        ConfigurationReportedAt = null;
        AppliedConfigurationRevision = null;
        AppliedHeartbeatSeconds = null;
        AppliedSettingsPollSeconds = null;
        AppliedBatchIntervalSeconds = null;
        ConfigurationAppliedAt = null;
        ConfigurationFailure = null;
    }
    public void BeginRotation(byte[] verifier, DateTimeOffset until) { PendingVerifier = verifier; PendingUntil = until.ToUniversalTime(); }
    public void FinishRotation() { Verifier = PendingVerifier!; PendingVerifier = null; PendingUntil = null; }
}

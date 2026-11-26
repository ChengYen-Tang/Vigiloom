using System.Text.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>Shared heartbeat and reported configuration state for an Agent.</summary>
public abstract class AgentStatus
{
    public DateTimeOffset? LastHeartbeatAt { get; private set; }
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
    public Guid? LogStatusReportEpoch { get; private set; }
    public long? LogStatusReportSequence { get; private set; }
    public DateTimeOffset? LogStatusReportedAt { get; private set; }
    public string? LogStatusJson { get; private set; }
    public Guid? InventoryConfigurationEpoch { get; private set; }
    public long? InventoryConfigurationSequence { get; private set; }
    public DateTimeOffset? InventoryConfigurationReportedAt { get; private set; }
    public string? InventoryConfigurationJson { get; private set; }
    public Guid? IntegrityConfigurationEpoch { get; private set; }
    public long? IntegrityConfigurationSequence { get; private set; }
    public DateTimeOffset? IntegrityConfigurationReportedAt { get; private set; }
    public string? IntegrityConfigurationJson { get; private set; }
    public DateTimeOffset? BaselineConfigurationReportedAt { get; private set; }
    public string? BaselineConfigurationJson { get; private set; }
    public string? RuntimeVersion { get; private set; }
    public string? RuntimeIdentifier { get; private set; }
    public DateTimeOffset? RuntimeReportedAt { get; private set; }
    public double? RuntimeCpuPercent { get; private set; }
    public long? RuntimeWorkingSetBytes { get; private set; }
    public string? RuntimeModuleFailuresJson { get; private set; }


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
    public void RecordLogStatus(AgentLogStatusReport? report, long sequence, DateTimeOffset now)
    {
        LogStatusReportEpoch = report is null ? null : QueueReportEpoch;
        LogStatusReportSequence = report is null ? null : sequence;
        LogStatusReportedAt = report is null ? null : now.ToUniversalTime();
        LogStatusJson = report is null ? null : JsonSerializer.Serialize(report);
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
    public void RecordInventoryConfiguration(InventorySettingsReport? report, long sequence, DateTimeOffset now)
    {
        InventoryConfigurationEpoch = report is null ? null : QueueReportEpoch;
        InventoryConfigurationSequence = report is null ? null : sequence;
        InventoryConfigurationReportedAt = report is null ? null : now.ToUniversalTime();
        InventoryConfigurationJson = report is null ? null : JsonSerializer.Serialize(report);
    }

    public void RecordIntegrityConfiguration(IntegritySettingsReport? report, long sequence, DateTimeOffset now)
    {
        IntegrityConfigurationEpoch = report is null ? null : QueueReportEpoch;
        IntegrityConfigurationSequence = report is null ? null : sequence;
        IntegrityConfigurationReportedAt = report is null ? null : now.ToUniversalTime();
        IntegrityConfigurationJson = report is null ? null : JsonSerializer.Serialize(report);
    }

    public void RecordBaselineConfiguration(BaselineSettingsReport? report, DateTimeOffset now)
    {
        BaselineConfigurationReportedAt = report is null ? null : now.ToUniversalTime();
        BaselineConfigurationJson = report is null ? null : JsonSerializer.Serialize(report);
    }
    public void RecordRuntime(AgentRuntimeReport? report, DateTimeOffset now)
    {
        RuntimeVersion = report?.Version;
        RuntimeIdentifier = report?.RuntimeIdentifier;
        RuntimeReportedAt = report is null ? null : now.ToUniversalTime();
        RuntimeCpuPercent = report?.CpuPercent;
        RuntimeWorkingSetBytes = report?.WorkingSetBytes;
        RuntimeModuleFailuresJson = report?.ModuleFailures is { } failures ? JsonSerializer.Serialize(failures) : null;
    }
    public void BeginQueueSession(Guid epoch)
    {
        RuntimeVersion = null;
        RuntimeIdentifier = null;
        RuntimeReportedAt = null;
        RuntimeCpuPercent = null;
        RuntimeWorkingSetBytes = null;
        RuntimeModuleFailuresJson = null;
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
        LogStatusReportEpoch = null;
        LogStatusReportSequence = null;
        LogStatusReportedAt = null;
        LogStatusJson = null;
        InventoryConfigurationEpoch = null;
        InventoryConfigurationSequence = null;
        InventoryConfigurationReportedAt = null;
        InventoryConfigurationJson = null;
        IntegrityConfigurationEpoch = null;
        IntegrityConfigurationSequence = null;
        IntegrityConfigurationReportedAt = null;
        IntegrityConfigurationJson = null;
        BaselineConfigurationReportedAt = null;
        BaselineConfigurationJson = null;
    }
}

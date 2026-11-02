using Vigiloom.BuildingBlocks.Identifiers;
using Vigiloom.Contracts.Agent;

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
    public int HeartbeatSafetyIntervalSeconds { get; private set; } = 30;
    public DateTimeOffset? QueueReportedAt { get; private set; }
    public Guid? QueueReportEpoch { get; private set; }
    public long? QueueReportSequence { get; private set; }
    public long? QueueTotalBytes { get; private set; }
    public long? QueueTotalBatches { get; private set; }
    public long? QueueQuarantinedBatches { get; private set; }
    public long? QueueCapacityBytes { get; private set; }
    public int? QueueCapacityBatches { get; private set; }
    public DateTimeOffset? ConfigurationReportedAt { get; private set; }
    public string? AppliedConfigurationRevision { get; private set; }
    public int? AppliedHeartbeatSeconds { get; private set; }
    public int? AppliedSettingsPollSeconds { get; private set; }
    public int? AppliedBatchIntervalSeconds { get; private set; }
    public DateTimeOffset? ConfigurationAppliedAt { get; private set; }
    public AgentConfigurationFailure? ConfigurationFailure { get; private set; }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now.ToUniversalTime();
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
    public bool RecordQueueStatus(long sequence, AgentQueueSnapshot? snapshot, DateTimeOffset now)
    {
        if (QueueReportSequence >= sequence) return false;
        QueueReportSequence = sequence;
        QueueReportedAt = now.ToUniversalTime();
        QueueTotalBytes = snapshot?.TotalBytes;
        QueueTotalBatches = snapshot?.TotalBatches;
        QueueQuarantinedBatches = snapshot?.QuarantinedBatches;
        QueueCapacityBytes = snapshot?.CapacityBytes;
        QueueCapacityBatches = snapshot?.CapacityBatches;
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

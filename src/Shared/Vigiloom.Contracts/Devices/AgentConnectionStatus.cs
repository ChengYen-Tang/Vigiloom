using System.Text.Json.Serialization;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Contracts.Devices;

/// <summary>Agent connectivity is independent of the device's Ping state.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentConnectionState>))]
public enum AgentConnectionState { Disabled, NotRegistered, AwaitingFirstHeartbeat, Online, Offline, ClockUncertain }

/// <summary>Non-secret Agent monitoring status for an authenticated reader.</summary>
public sealed record AgentConnectionStatus(Guid DeviceId, AgentConnectionState State, DateTimeOffset? LastHeartbeatAt,
    int OfflineAfterSeconds, AgentQueueMonitoring? Queue = null, DateTimeOffset? LastCommittedBatchAt = null,
    AgentBatchTimeObservation? LastBatchTime = null, AgentReceiptCapacity? ReceiptCapacity = null,
    AgentReplayIssueObservation? LastReplayIssue = null);

[JsonConverter(typeof(JsonStringEnumConverter<AgentReplayIssue>))]
public enum AgentReplayIssue { ClockUncertain = 1, TimestampAhead = 2, Expired = 3 }

/// <summary>A historical central rejection, not proof that the issue is still active.</summary>
public sealed record AgentReplayIssueObservation(AgentReplayIssue Reason, DateTimeOffset ObservedAt);

/// <summary>Central retained receipt usage; original payload lengths are not database disk usage.</summary>
public sealed record AgentReceiptCapacity(long RetainedBatches, long OriginalPayloadBytes, int MaximumBatches, long MaximumPayloadBytes);

/// <summary>Historical timestamps from one committed batch; not a measurement of the current clock.</summary>
public sealed record AgentBatchTimeObservation(DateTimeOffset EnqueuedAt, DateTimeOffset ReceivedAt, bool EnqueuedAheadOfReceipt);

/// <summary>Most recent queue report received by the platform; null snapshot means the Agent could not read the queue.</summary>
public sealed record AgentQueueMonitoring(DateTimeOffset ReportedAt, AgentQueueSnapshot? Snapshot, AgentQuarantineBreakdown? Quarantine = null);

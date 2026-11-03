using System.Text.Json.Serialization;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Contracts.Devices;

/// <summary>Agent connectivity is independent of the device's Ping state.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AgentConnectionState>))]
public enum AgentConnectionState { Disabled, NotRegistered, AwaitingFirstHeartbeat, Online, Offline, ClockUncertain }

/// <summary>Non-secret Agent monitoring status for an authenticated reader.</summary>
public sealed record AgentConnectionStatus(Guid DeviceId, AgentConnectionState State, DateTimeOffset? LastHeartbeatAt,
    int OfflineAfterSeconds, AgentQueueMonitoring? Queue = null, DateTimeOffset? LastCommittedBatchAt = null);

/// <summary>Most recent queue report received by the platform; null snapshot means the Agent could not read the queue.</summary>
public sealed record AgentQueueMonitoring(DateTimeOffset ReportedAt, AgentQueueSnapshot? Snapshot);

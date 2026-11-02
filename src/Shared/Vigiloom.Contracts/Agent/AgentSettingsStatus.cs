namespace Vigiloom.Contracts.Agent;

/// <summary>Observed transport scheduling state; Agent and device availability are separate.</summary>
public enum AgentSettingsApplyState { Disabled, NotRegistered, WaitingForHeartbeat, WaitingForReport, Pending, Applied, Failed, Offline, ClockUncertain, ReportStale }

/// <summary>Desired settings and the most recent non-secret report from the Agent.</summary>
public sealed record AgentSettingsStatus(Guid DeviceId, AgentSettingsApplyState State, AgentSettingsSnapshot Desired,
    AgentSettingsSnapshot? Applied, DateTimeOffset? AppliedAt, DateTimeOffset? ReportedAt,
    AgentConfigurationFailure? Failure);

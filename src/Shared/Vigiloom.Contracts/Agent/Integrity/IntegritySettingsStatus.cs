namespace Vigiloom.Contracts.Agent.Integrity;

/// <summary>Desired policy revision and the last policy observed by the Agent worker.</summary>
public sealed record IntegritySettingsStatus(Guid DeviceId, AgentSettingsApplyState State,
    string DesiredRevision, long DesiredGeneration, string? AppliedRevision, long? AppliedGeneration,
    DateTimeOffset? AppliedAt, DateTimeOffset? ReportedAt, AgentConfigurationFailure? Failure);

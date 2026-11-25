namespace Vigiloom.Contracts.Agent;

public enum AgentRegistrationStatus { Success, Invalid, Conflict, NotFound }

/// <summary>A short-lived one-time Agent enrollment credential.</summary>
public sealed record AgentEnrollmentIssue(AgentRegistrationStatus Status, string? Secret = null, DateTimeOffset? ExpiresAt = null);
public sealed record AgentEnrollmentResult(AgentRegistrationStatus Status, Guid? AgentId = null);
public sealed record RegisteredAgentIdentity(Guid AgentId, Guid DeviceId, long DeviceGeneration);
public sealed record AgentEnrollmentCommand(Guid ActorId);
public sealed record AgentRedeemCommand(string? Secret, byte[]? MachineVerifier);
public sealed record AgentCredentialCommand(string? Credential);
public sealed record AgentRevokeCommand(Guid ActorId);
public sealed record AgentRegistrationChanged(Guid AgentId, Guid DeviceId, bool Revoked);
public sealed record AgentRegistrationAudit(Guid DeviceId, Guid? AgentId, Guid? ActorId, string Action, DateTimeOffset OccurredAt);

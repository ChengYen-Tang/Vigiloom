namespace Vigiloom.Contracts.Agent;

/// <summary>Notifies host monitoring that an Agent's desired version changed.</summary>
public sealed record AgentAssignmentChanged(Guid AgentId, Guid? UpdateId);

/// <summary>Internal request from an authorized management operation.</summary>
public sealed record AgentVersionAssignmentCommand(Guid? PackageId, string Revision, Guid ActorId);

/// <summary>Records an administrator's change to an Agent's desired version.</summary>
public sealed record AgentVersionAssignmentAudit(Guid DeviceId, Guid AgentId, Guid ActorId,
    Guid? PreviousPackageId, Guid? PackageId, DateTimeOffset OccurredAt);

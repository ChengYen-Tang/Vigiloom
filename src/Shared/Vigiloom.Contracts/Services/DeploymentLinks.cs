namespace Vigiloom.Contracts.Services;

/// <summary>A privately visible, administrator-attested deployment relationship.</summary>
public sealed record DeploymentLinkView(Guid Id, Guid DeviceId, string DeviceName, Guid ServiceInstanceId, string InstanceName,
    string Evidence, DateTimeOffset StartedAt, string Version, bool DeviceArchived, bool InstanceRetired, bool SourceRevoked, bool ServiceStopped);
public sealed record DeploymentLinkPage(IReadOnlyList<DeploymentLinkView> Items, bool HasMore);
public sealed record CreateDeploymentLink(Guid DeviceId, Guid ServiceInstanceId, string Evidence);
public sealed record EndDeploymentLink(string Version);

/// <summary>Result of an internal deployment relationship mutation.</summary>
public enum DeploymentLinkMutationStatus { Saved, NotFound, Conflict, Invalid }
/// <summary>Mutation status and identifier of a newly created relationship, if any.</summary>
public sealed record DeploymentLinkMutationResult(DeploymentLinkMutationStatus Status, Guid? Id = null);
/// <summary>Administrator request to create a relationship in the resource service.</summary>
public sealed record CreateDeploymentLinkCommand(CreateDeploymentLink Input, Guid ActorId);
/// <summary>Administrator request to end a relationship in the resource service.</summary>
public sealed record EndDeploymentLinkCommand(EndDeploymentLink Input, Guid ActorId);

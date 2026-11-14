namespace Vigiloom.Contracts.Services;

/// <summary>A privately visible, administrator-attested deployment relationship.</summary>
public sealed record DeploymentLinkView(Guid Id, Guid DeviceId, string DeviceName, Guid ServiceInstanceId, string InstanceName,
    string Evidence, DateTimeOffset StartedAt, string Version, bool DeviceArchived, bool InstanceRetired, bool SourceRevoked, bool ServiceStopped);
public sealed record DeploymentLinkPage(IReadOnlyList<DeploymentLinkView> Items, bool HasMore);
public sealed record CreateDeploymentLink(Guid DeviceId, Guid ServiceInstanceId, string Evidence);
public sealed record EndDeploymentLink(string Version);

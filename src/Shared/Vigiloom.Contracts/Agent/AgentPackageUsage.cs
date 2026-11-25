namespace Vigiloom.Contracts.Agent;

/// <summary>Checks whether an active Agent reports a package version as installed.</summary>
public sealed record AgentPackageUsageRequest(string Version, string RuntimeIdentifier);
public sealed record AgentPackageUsageResponse(bool InUse);
public enum AgentPackageRemovalResult { Removed, InUse, NotFound }

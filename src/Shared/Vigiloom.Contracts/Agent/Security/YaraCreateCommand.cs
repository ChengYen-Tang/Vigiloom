namespace Vigiloom.Contracts.Agent.Security;

/// <summary>A scan request after the device and Agent identity have been checked by the caller.</summary>
public sealed record YaraCreateCommand(RegisteredAgentIdentity Identity, YaraScanRequest Request, Guid ActorId);

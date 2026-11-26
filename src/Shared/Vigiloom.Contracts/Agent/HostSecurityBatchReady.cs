namespace Vigiloom.Contracts.Agent;

/// <summary>Identifies an Agent batch retained by Host for security processing.</summary>
public sealed record HostSecurityBatchReady(Guid AgentId, Guid BatchId, Guid DeviceId, long DeviceGeneration, string Kind);

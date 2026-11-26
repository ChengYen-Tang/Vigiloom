namespace Vigiloom.Contracts.Agent.Security;

/// <summary>Notifies the Agent that its saved scan work may have changed.</summary>
public sealed record YaraScanChanged(Guid AgentId);

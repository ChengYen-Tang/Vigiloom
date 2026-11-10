using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Configuration;

/// <summary>Associates a non-secret log status with the identity that observed it.</summary>
public sealed record AgentLogStatusObservation(AgentSettingsOwner Owner, AgentLogStatusReport Report);

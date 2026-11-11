using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Configuration;

public sealed record InventorySettingsObservation(AgentSettingsOwner Owner, InventorySettingsReport Report);

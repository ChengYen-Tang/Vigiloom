namespace Vigiloom.Contracts.Agent.Inventory;

/// <summary>Resolves the currently enrolled Agent for an eligible device without exposing credentials.</summary>
public interface IAgentOwnerResolver
{
    Task<InventoryOwner?> ResolveAsync(Guid deviceId, CancellationToken token);
}

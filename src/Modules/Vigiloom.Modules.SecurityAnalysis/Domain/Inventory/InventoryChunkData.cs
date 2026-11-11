using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;

/// <summary>One staged source chunk, addressed by the source generation and index.</summary>
public sealed class InventoryChunkData
{
    private InventoryChunkData() { }
    public InventoryChunkData(Guid agentId, InventoryChunk chunk, byte[] payload)
    {
        AgentId = agentId; Source = chunk.Source; Generation = chunk.Generation; ChunkIndex = chunk.ChunkIndex; Payload = payload;
    }

    public long RowId { get; private set; }
    public Guid AgentId { get; private set; }
    public InventorySource Source { get; private set; }
    public long Generation { get; private set; }
    public int ChunkIndex { get; private set; }
    public byte[] Payload { get; private set; } = [];
}

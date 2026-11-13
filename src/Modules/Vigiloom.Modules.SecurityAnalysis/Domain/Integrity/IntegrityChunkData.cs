namespace Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;

public sealed class IntegrityChunkData
{
    public long RowId { get; private set; }
    public Guid AgentId { get; set; }
    public Guid ScopeId { get; set; }
    public long Generation { get; set; }
    public int ChunkIndex { get; set; }
    public byte[] Payload { get; set; } = [];
}

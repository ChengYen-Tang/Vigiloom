namespace Vigiloom.Modules.AgentManagement.Domain;

public sealed class UpdateTrustState
{
    public Guid Id { get; set; }
    public long RowId { get; set; }
    public int Scope { get; set; } = 1;
    public byte[] Snapshot { get; set; } = [];
    public byte[] Digest { get; set; } = [];
}

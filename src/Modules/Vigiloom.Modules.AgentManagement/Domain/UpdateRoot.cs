namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>An immutable, authenticated root needed to bring an offline Agent forward.</summary>
public sealed class UpdateRoot
{
    public long RowId { get; set; }
    public int Version { get; set; }
    public byte[] Metadata { get; set; } = [];
    public byte[] Digest { get; set; } = [];
}

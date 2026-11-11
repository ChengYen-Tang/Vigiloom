namespace Vigiloom.Modules.AgentManagement.Domain;

public sealed class AgentPackageSource
{
    public Guid Id { get; set; }
    public long RowId { get; set; }
    public int Scope { get; set; } = 1;
    public required string BaseUri { get; set; }
    public string? ProtectedCredential { get; set; }
    public byte[] Version { get; set; } = [];
}

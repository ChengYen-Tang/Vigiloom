namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>The immutable target of an update, retained after cancellation or reassignment.</summary>
public sealed class AgentUpdateTarget
{
    public Guid Id { get; set; }
    public long RowId { get; set; }
    public Guid AgentId { get; set; }
    public Guid PackageId { get; set; }
    public required string Version { get; set; }
    public required string RuntimeIdentifier { get; set; }
    public required string ManifestSha256 { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
}

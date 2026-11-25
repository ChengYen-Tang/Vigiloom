namespace Vigiloom.Modules.Resources.Domain;

/// <summary>The package identity recorded for an update request.</summary>
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

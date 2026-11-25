namespace Vigiloom.Modules.Resources.Domain;

/// <summary>A release selected for the platform's offline Agent package library.</summary>
public sealed class AgentPackage
{
    public Guid Id { get; set; }
    public long RowId { get; set; }
    public required string Version { get; set; }
    public required string RuntimeIdentifier { get; set; }
    public required string Sha256 { get; set; }
    public required string ManifestSha256 { get; set; }
    public long Size { get; set; }
}

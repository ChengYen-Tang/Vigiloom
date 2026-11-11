namespace Vigiloom.Modules.AgentManagement.Domain;

public sealed class AgentPackage
{
    public Guid Id { get; set; }
    public long RowId { get; set; }
    public required string Version { get; set; }
    public required string RuntimeIdentifier { get; set; }
    public required string Sha256 { get; set; }
    public required string ManifestSha256 { get; set; }
    public long Size { get; set; }
    public bool Revoked { get; set; }
    public bool Listed { get; set; }
    public DateTimeOffset VerifiedUntil { get; set; }
}

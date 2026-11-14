using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>An immutable validated upstream artifact, retained independently of activation.</summary>
public sealed class IntelligenceRevision
{
    public Guid Id { get; set; } = ResourceIds.Create();
    public long RowId { get; private set; }
    public Guid SourceId { get; set; }
    public required string UpstreamVersion { get; set; }
    public DateTimeOffset ReleasedAt { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
    public required byte[] Digest { get; set; }
    public required byte[] Artifact { get; set; }
    public required string SchemaVersion { get; set; }
    public required string MappingVersion { get; set; }
    public required string LicenseEvidence { get; set; }
    public int RecordCount { get; set; }
    public bool Revoked { get; set; }
    public bool Normalized { get; set; }
}

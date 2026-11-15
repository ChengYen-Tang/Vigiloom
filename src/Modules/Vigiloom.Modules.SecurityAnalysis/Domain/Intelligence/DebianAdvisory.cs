namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>A release-specific package advisory within an immutable Debian source revision.</summary>
public sealed class DebianAdvisory
{
    public long RowId { get; private set; }
    public Guid RevisionId { get; set; }
    public required string SourcePackage { get; set; }
    public required string AdvisoryId { get; set; }
    public required string Release { get; set; }
    public int State { get; set; }
    public string? FixedVersion { get; set; }
}

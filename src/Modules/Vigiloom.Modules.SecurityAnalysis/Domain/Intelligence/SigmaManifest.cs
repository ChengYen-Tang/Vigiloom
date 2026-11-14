namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>An immutable source-authorization manifest; it does not claim executable detections.</summary>
public sealed class SigmaManifest
{
    public long RowId { get; private set; }
    public Guid RevisionId { get; set; }
    public required string PolicyVersion { get; set; }
    public int IncludedCount { get; set; }
    public int ExcludedCount { get; set; }
    public required byte[] Digest { get; set; }
}

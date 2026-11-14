namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>A CISA KEV entry in one immutable source revision.</summary>
public sealed class KevRecord
{
    public long RowId { get; private set; }
    public Guid RevisionId { get; set; }
    public required string CveId { get; set; }
    public required string VendorProject { get; set; }
    public required string Product { get; set; }
    public required string VulnerabilityName { get; set; }
    public DateOnly DateAdded { get; set; }
    public required string Description { get; set; }
    public required string RequiredAction { get; set; }
    public DateOnly DueDate { get; set; }
    public string? RansomwareUse { get; set; }
    public string? ForensicTriage { get; set; }
    public string? Notes { get; set; }
    public string? CwesJson { get; set; }
}

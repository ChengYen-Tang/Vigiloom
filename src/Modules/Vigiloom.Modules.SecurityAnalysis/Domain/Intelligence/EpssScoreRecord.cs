namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>One EPSS score within one immutable intelligence revision.</summary>
public sealed class EpssScoreRecord
{
    public long RowId { get; private set; }
    public Guid RevisionId { get; set; }
    public required string Cve { get; set; }
    public decimal Probability { get; set; }
    public decimal Percentile { get; set; }
}

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Detection;

/// <summary>Tracks the retained time range and incomplete windows for one activation period.</summary>
public sealed class SigmaCorrelationCoverage
{
    public long RowId { get; private set; }
    public Guid TestId { get; set; }
    public required byte[] ActivationVersion { get; set; }
    public DateTimeOffset CompleteSince { get; set; }
    public DateTimeOffset? IncompleteUntil { get; set; }
}

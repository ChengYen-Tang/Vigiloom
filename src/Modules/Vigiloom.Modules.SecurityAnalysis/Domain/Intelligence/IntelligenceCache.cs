namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>Conditional request validators bound to their verified source revision.</summary>
public sealed class IntelligenceCache
{
    public Guid SourceId { get; set; }
    public Guid RevisionId { get; set; }
    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
}

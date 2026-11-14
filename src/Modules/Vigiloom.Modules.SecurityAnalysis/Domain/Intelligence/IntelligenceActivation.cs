namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>Current revision selected for a source.</summary>
public sealed class IntelligenceActivation
{
    public Guid SourceId { get; set; }
    public Guid RevisionId { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public byte[] Version { get; private set; } = [];
}

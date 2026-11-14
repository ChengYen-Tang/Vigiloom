namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>Durable record of a source revision selection.</summary>
public sealed class IntelligenceActivationEvent
{
    public long RowId { get; private set; }
    public Guid SourceId { get; set; }
    public Guid? PreviousRevisionId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid? ActorId { get; set; }
    public bool Scheduled { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public bool LargeReductionConfirmed { get; set; }
}

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>An administrator-provided copy of a source snapshot; origin authenticity is not implied.</summary>
public sealed class OfflineImport
{
    public long RowId { get; private set; }
    public Guid SourceId { get; set; }
    public Guid RevisionId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset ImportedAt { get; set; }
}

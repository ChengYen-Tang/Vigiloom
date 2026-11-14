namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>An advisory included in a retained revision, referring to its immutable source entry.</summary>
public sealed class AdvisoryRevisionEntry
{
    public long RowId { get; private set; }
    public Guid RevisionId { get; set; }
    public long EntryRowId { get; set; }
    public required string AdvisoryId { get; set; }
}

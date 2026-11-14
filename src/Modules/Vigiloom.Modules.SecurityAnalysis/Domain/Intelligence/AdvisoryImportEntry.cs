namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>The latest observed version of an advisory in an incomplete import.</summary>
public sealed class AdvisoryImportEntry
{
    public long RowId { get; private set; }
    public Guid ImportId { get; set; }
    public long PageRowId { get; set; }
    public required string AdvisoryId { get; set; }
    public required byte[] Digest { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
    public int Ordinal { get; set; }
}

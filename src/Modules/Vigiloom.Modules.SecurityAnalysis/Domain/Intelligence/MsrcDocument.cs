namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>One monthly upstream artifact retained within a complete source revision.</summary>
public sealed class MsrcDocument
{
    public long RowId { get; private set; }
    public Guid RevisionId { get; set; }
    public required string DocumentId { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public required byte[] Digest { get; set; }
}

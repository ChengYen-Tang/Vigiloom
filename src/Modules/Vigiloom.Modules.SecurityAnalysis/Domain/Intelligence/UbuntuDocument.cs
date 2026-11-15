namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>A content-addressed upstream document and the mapping used to index it.</summary>
public sealed class UbuntuDocument
{
    public long RowId { get; private set; }
    public required byte[] Digest { get; set; }
    public required string MappingVersion { get; set; }
    public required string AdvisoryId { get; set; }
    public required byte[] Compressed { get; set; }
    public int OriginalLength { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public DateTimeOffset? WithdrawnAt { get; set; }
}

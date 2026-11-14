namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>An immutable original response page in one import generation.</summary>
public sealed class AdvisoryImportPage
{
    public long RowId { get; private set; }
    public Guid ImportId { get; set; }
    public int Number { get; set; }
    public int Phase { get; set; }
    public bool Reconciliation { get; set; }
    public required byte[] Digest { get; set; }
    public required byte[] Payload { get; set; }
    public string? NextCursor { get; set; }
}

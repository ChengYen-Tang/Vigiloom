namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>Candidate advisory location within a content-addressed MSRC document.</summary>
public sealed class MsrcCveIndex
{
    public long RowId { get; private set; }
    public required byte[] Digest { get; set; }
    public required string Cve { get; set; }
}

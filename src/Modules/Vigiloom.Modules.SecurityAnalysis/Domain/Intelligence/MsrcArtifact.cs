namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>Content-addressed upstream document shared by source revisions.</summary>
public sealed class MsrcArtifact
{
    public long RowId { get; private set; }
    public required byte[] Digest { get; set; }
    public required byte[] Content { get; set; }
    // -1 denotes a legacy artifact whose candidate index has not been built.
    public int IndexedCveCount { get; set; } = -1;
    public int IndexedProductCount { get; set; } = -1;
    public bool ProductIndexComplete { get; set; }
}

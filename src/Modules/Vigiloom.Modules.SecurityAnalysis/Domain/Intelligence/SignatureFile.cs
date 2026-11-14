namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>Original source material retained without asserting rule support or license eligibility.</summary>
public sealed class SignatureFile
{
    public long RowId { get; private set; }
    public Guid RevisionId { get; set; }
    public required string Path { get; set; }
    public int Kind { get; set; }
    public required byte[] Digest { get; set; }
    public required byte[] Content { get; set; }
}

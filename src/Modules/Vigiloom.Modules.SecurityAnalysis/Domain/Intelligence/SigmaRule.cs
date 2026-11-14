namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>One retained upstream rule in a validated immutable source revision.</summary>
public sealed class SigmaRule
{
    public long RowId { get; private set; }
    public Guid RevisionId { get; set; }
    public required string Path { get; set; }
    public string? RuleId { get; set; }
    public required string Title { get; set; }
    public string? Author { get; set; }
    public string? Status { get; set; }
    public string? License { get; set; }
    public string? Product { get; set; }
    public string? Category { get; set; }
    public string? Service { get; set; }
    public required byte[] Digest { get; set; }
    public required byte[] Content { get; set; }
}

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Detection;

/// <summary>Administrator-selected rule revision, separate from the upstream source file.</summary>
public sealed class SigmaRuleActivation
{
    public Guid Id { get; set; }
    public long RowId { get; private set; }
    public Guid SourceId { get; set; }
    public required string Path { get; set; }
    public Guid TestId { get; set; }
    public bool Enabled { get; set; }
    public Guid UpdatedBy { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public byte[] Version { get; private set; } = [];
}

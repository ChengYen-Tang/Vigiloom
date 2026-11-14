namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>One classified upstream line; a classified indicator is not a detection or publication authorization.</summary>
public sealed class IocEntry
{
    public long RowId { get; private set; }
    public long SignatureFileRowId { get; set; }
    public int LineNumber { get; set; }
    public required string Kind { get; set; }
    public string? Value { get; set; }
    public string? Reason { get; set; }
}

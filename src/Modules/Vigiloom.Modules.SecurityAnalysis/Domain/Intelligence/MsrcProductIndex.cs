namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>Product-scoped CVE candidate in a content-addressed vendor document.</summary>
public sealed class MsrcProductIndex
{
    public long RowId { get; private set; }
    public required byte[] Digest { get; set; }
    public required byte[] ProductHash { get; set; }
    public required string ProductName { get; set; }
    public required string Cve { get; set; }
}

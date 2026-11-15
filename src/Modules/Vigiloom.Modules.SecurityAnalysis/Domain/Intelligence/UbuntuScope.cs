namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>A package discovery key for one version of a source document.</summary>
public sealed class UbuntuScope
{
    public long RowId { get; private set; }
    public required byte[] Digest { get; set; }
    public required string MappingVersion { get; set; }
    public required string Ecosystem { get; set; }
    public required string SourcePackage { get; set; }
}

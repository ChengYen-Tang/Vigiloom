namespace Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

/// <summary>Associates an immutable source revision with a particular document version.</summary>
public sealed class UbuntuRevisionDocument
{
    public Guid RevisionId { get; set; }
    public required byte[] Digest { get; set; }
    public required string MappingVersion { get; set; }
    public required string AdvisoryId { get; set; }
}

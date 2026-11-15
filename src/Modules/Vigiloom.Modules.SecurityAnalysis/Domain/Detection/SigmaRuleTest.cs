namespace Vigiloom.Modules.SecurityAnalysis.Domain.Detection;

/// <summary>An immutable, attributed replay result; samples contain references and outcomes, not raw logs.</summary>
public sealed class SigmaRuleTest
{
    public Guid Id { get; set; }
    public long RowId { get; private set; }
    public Guid SourceId { get; set; }
    public Guid RevisionId { get; set; }
    public required string Path { get; set; }
    public required string Digest { get; set; }
    public required string ActivationVersion { get; set; }
    public required string MatcherVersion { get; set; }
    public required string SamplesJson { get; set; }
    public bool Passed { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset TestedAt { get; set; }
}

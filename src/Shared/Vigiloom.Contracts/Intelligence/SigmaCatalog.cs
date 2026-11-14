namespace Vigiloom.Contracts.Intelligence;

/// <summary>Availability of an attributed source subset; this does not imply detection coverage.</summary>
public enum SigmaAvailability { Ready, Stale, Disabled, NoActiveRevision, Unavailable, SelectionChanged }

public sealed record SigmaSnapshot(SigmaAvailability Availability, Guid? SourceId, Guid? RevisionId,
    string? ActivationVersion, string? Commit, string? ManifestSha256, int IncludedCount, int ExcludedCount);

public sealed record SigmaSourceRule(string Path, string Title, string? Id, string Author, string License,
    string LicenseUrl, string SourceUrl, string Sha256);

public sealed record SigmaSourcePage(SigmaAvailability Availability, IReadOnlyList<SigmaSourceRule> Rules, string? NextPath);
public sealed record SigmaSourceContent(SigmaAvailability Availability, SigmaSourceRule? Rule, byte[]? Content);

/// <summary>
/// Reads only the attributed, authorized core source subset, not executable detections.
/// Consumers must check rule compatibility and coordinate a final selection check with publishing results.
/// </summary>
public interface ISigmaCatalog
{
    Task<SigmaSnapshot> GetActiveAsync(CancellationToken cancellationToken);
    Task<SigmaSourcePage> ReadPageAsync(SigmaSnapshot selection, string? afterPath, CancellationToken cancellationToken);
    Task<SigmaSourceContent> ReadRuleAsync(SigmaSnapshot selection, string path, CancellationToken cancellationToken);
}

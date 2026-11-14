namespace Vigiloom.Contracts.Intelligence;

/// <summary>State of the GitHub-reviewed source; absence is not evidence that an asset is safe.</summary>
public enum AdvisoryAvailability { Ready, Stale, Disabled, NoActiveRevision, Unavailable, SelectionChanged }

public sealed record AdvisorySnapshot(AdvisoryAvailability Availability, Guid? SourceId, Guid? RevisionId,
    string? ActivationVersion, string? UpstreamVersion, DateTimeOffset? LastSuccessfulCheckAt);

/// <summary>Upstream package ranges are preserved without interpreting ecosystem version semantics.</summary>
public sealed record AdvisoryPackageView(string Ecosystem, string Name, string VulnerableRange, string? FirstPatchedVersion);

public sealed record AdvisoryEntryView(string Id, string? CveId, DateTimeOffset PublishedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? WithdrawnAt, IReadOnlyList<AdvisoryPackageView> Packages);

public sealed record AdvisoryLookupResult(AdvisoryAvailability Availability, Guid SourceId, Guid RevisionId,
    bool Historical, IReadOnlyList<AdvisoryEntryView> Entries);

/// <summary>
/// Bounded lookup of the GitHub-reviewed subset by known GHSA identifier. Results do not establish package
/// applicability, complete coverage, or a clean asset. Consumers must preserve the source and revision and
/// coordinate a final current-selection check with publication of their evaluation.
/// </summary>
public interface IAdvisoryCatalog
{
    Task<AdvisorySnapshot> GetActiveAsync(CancellationToken cancellationToken);
    Task<AdvisoryLookupResult> FindCurrentAsync(Guid sourceId, Guid revisionId, string activationVersion,
        IReadOnlyCollection<string> advisoryIds, CancellationToken cancellationToken);
    Task<AdvisoryLookupResult> FindHistoricalAsync(Guid sourceId, Guid revisionId,
        IReadOnlyCollection<string> advisoryIds, CancellationToken cancellationToken);
}

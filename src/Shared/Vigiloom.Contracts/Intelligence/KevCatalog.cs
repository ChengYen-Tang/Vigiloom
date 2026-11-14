namespace Vigiloom.Contracts.Intelligence;

/// <summary>Availability of the KEV dataset; a missing entry is not a clean verdict.</summary>
public enum KevAvailability { Ready, Stale, Disabled, NoActiveRevision, Unavailable, SelectionChanged }

/// <summary>A version reference to retain throughout one evaluation. Origin is official-https or administrator-import-unverified when known.</summary>
public sealed record KevSnapshot(KevAvailability Availability, Guid? SourceId, Guid? RevisionId,
    string? ActivationVersion, string? UpstreamVersion, DateTimeOffset? ReleasedAt, DateTimeOffset? LastSuccessfulCheckAt,
    string? Origin = null, string? MappingVersion = null, string? LicenseEvidence = null);

/// <summary>One source entry; upstream product text alone does not establish local applicability.</summary>
public sealed record KevEntry(string CveId, string VendorProject, string Product, string VulnerabilityName,
    DateOnly DateAdded, string Description, string RequiredAction, DateOnly DueDate,
    string? RansomwareUse, string? ForensicTriage, string? Notes, IReadOnlyList<string>? Cwes);

/// <summary>Results for one fixed revision, including origin and license evidence. An empty list never asserts that an asset is safe.</summary>
public sealed record KevLookupResult(KevAvailability Availability, Guid SourceId, Guid RevisionId, bool Historical, IReadOnlyList<KevEntry> Entries,
    string? Origin = null, string? MappingVersion = null, string? LicenseEvidence = null);

/// <summary>
/// Version-aware central lookup for KEV data. Consumers must discard all partial results if any current lookup
/// is not Ready and recheck the same selection immediately before publishing an evaluation. An empty current
/// lookup can be used for that check, but a separate lookup is not atomic with the consumer's database commit;
/// the consumer must coordinate a final check and publication transaction. This interface does not perform
/// vulnerability applicability analysis.
/// </summary>
public interface IKevCatalog
{
    Task<KevSnapshot> GetActiveAsync(CancellationToken cancellationToken);
    Task<KevLookupResult> FindCurrentAsync(Guid sourceId, Guid revisionId, string activationVersion,
        IReadOnlyCollection<string> cveIds, CancellationToken cancellationToken);
    Task<KevLookupResult> FindHistoricalAsync(Guid sourceId, Guid revisionId, IReadOnlyCollection<string> cveIds, CancellationToken cancellationToken);
}

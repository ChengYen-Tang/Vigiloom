namespace Vigiloom.Contracts.Intelligence;

/// <summary>Availability of the reviewed IOC source files, independently of any matcher or YARA rules.</summary>
public enum IocSourceAvailability { Ready, Stale, Disabled, NoActiveRevision, Unavailable, SelectionChanged }

public sealed record IocSourceSnapshot(IocSourceAvailability Availability, Guid? SourceId, Guid? RevisionId,
    string? ActivationVersion, string? Commit);

public sealed record IocSourceFile(IocSourceAvailability Availability, string? Path, string? Sha256,
    string? SourceUrl, string? License, string? LicenseUrl, string? LicenseEvidenceUrl, byte[]? Content);

/// <summary>
/// Provides reviewed original IOC files, including source comments and attribution. Consumers own parsing,
/// source-context attribution and a final coordinated selection check before publishing results.
/// </summary>
public interface IIocSource
{
    Task<IocSourceSnapshot> GetActiveAsync(CancellationToken cancellationToken);
    Task<IocSourceFile> ReadFileAsync(IocSourceSnapshot selection, string path, CancellationToken cancellationToken);
}

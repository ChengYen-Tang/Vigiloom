namespace Vigiloom.Contracts.Intelligence;

/// <summary>A reviewed, read-only baseline check selected by the central platform.</summary>
public sealed record BaselineCheck(string Id, string Title, string Platform, string[] Versions,
    string Operation, string Target, string Expected, string SourceUrl, string License);

public enum BaselineSourceAvailability { Ready, Disabled, Stale, NoActiveRevision, Unavailable }

/// <summary>Revision and activation identify the exact centrally selected rule data.</summary>
public sealed record BaselineSourceSnapshot(BaselineSourceAvailability Availability, Guid? SourceId,
    Guid? RevisionId, string? ActivationVersion, string? UpstreamVersion, BaselineCheck? Check);

public interface IBaselineSource
{
    Task<BaselineSourceSnapshot> GetActiveAsync(string sourceKey, CancellationToken cancellationToken);
}

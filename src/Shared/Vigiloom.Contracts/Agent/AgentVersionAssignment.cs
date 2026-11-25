using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

/// <summary>A desired package, which is not evidence that the Agent has installed it.</summary>
public sealed record AgentVersionAssignmentView(Guid AgentId, Guid? PackageId, string? PackageVersion,
    string? RuntimeIdentifier, DateTimeOffset AssignedAt, string Revision, bool Available,
    string? ReportedVersion = null, string? ReportedRuntimeIdentifier = null, DateTimeOffset? ReportedAt = null,
    AgentUpdateResultView? LatestResult = null);

/// <summary>The latest received stage or terminal result for one previously assigned update.</summary>
public sealed record AgentUpdateResultView(Guid UpdateId, string PackageVersion, AgentUpdateResultKind Result,
    DateTimeOffset OccurredAt, DateTimeOffset ReceivedAt, bool RetryAllowed, AgentUpdateFailure? Failure);

/// <summary>A null package cancels the current assignment.</summary>
public sealed record AgentVersionAssignmentUpdate([property: JsonRequired] Guid? PackageId, string Revision);

/// <summary>The authenticated Agent's current desired package, not authorization to download it.</summary>
public sealed record AgentVersionTarget(Guid UpdateId, Guid PackageId, string PackageVersion,
    string RuntimeIdentifier, string ManifestSha256, string Revision)
{
    public bool IsValid()
    {
        Span<byte> revision = stackalloc byte[8];
        return UpdateId.Version == 7 && PackageId.Version == 7 && PackageVersion is { Length: > 0 and <= 128 } &&
            RuntimeIdentifier is "win-x64" or "win-x86" or "linux-x64" &&
            ManifestSha256 is { Length: 64 } && ManifestSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            Revision is { Length: 12 } && Convert.TryFromBase64String(Revision, revision, out var written) && written == revision.Length;
    }
}

public enum VersionAssignmentResult { Saved, NotFound, Conflict }

/// <summary>An immutable update target, available after reassignment or cancellation.</summary>
public sealed record RecordedAgentUpdateTarget(Guid UpdateId, Guid AgentId, Guid PackageId, string PackageVersion,
    string RuntimeIdentifier, string ManifestSha256, DateTimeOffset AssignedAt);

/// <summary>Agent-reported version and latest update result from host monitoring.</summary>
public sealed record AgentVersionObservation(string? ReportedVersion, string? ReportedRuntimeIdentifier,
    DateTimeOffset? ReportedAt, AgentUpdateResultView? LatestResult);

public interface IAgentVersionClient
{
    Task<AgentVersionAssignmentView?> GetAsync(Guid agentId, CancellationToken token);
    Task<AgentPackagePage> PackagesAsync(int page, CancellationToken token);
    Task<VersionAssignmentResult> SetAsync(Guid agentId, AgentVersionAssignmentUpdate update, CancellationToken token);
}

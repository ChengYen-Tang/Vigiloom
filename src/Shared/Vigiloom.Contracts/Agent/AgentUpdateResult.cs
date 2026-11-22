using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

public enum AgentUpdateResultKind
{
    Succeeded = 1, RolledBack = 2, RollbackFailed = 3,
    Prepared = 4, Stopping = 5, Switching = 6, Starting = 7, Verifying = 8, RollingBack = 9, Failed = 10
}
public enum AgentUpdateFailure { Interrupted = 1, AuthorizationUnavailable = 2, Other = 3 }

/// <summary>An update observation; terminal results are retained for delivery, while intermediate stages are best effort.</summary>
public sealed record AgentUpdateResult([property: JsonRequired] Guid MessageId, [property: JsonRequired] Guid UpdateId,
    [property: JsonRequired] Guid PackageId, [property: JsonRequired] string ManifestSha256, [property: JsonRequired] int Sequence,
    [property: JsonRequired] AgentUpdateResultKind Result, [property: JsonRequired] DateTimeOffset OccurredAt,
    [property: JsonRequired] bool RetryAllowed, [property: JsonRequired] AgentUpdateFailure? Failure)
{
    public bool IsValid() => MessageId.Version == 7 && UpdateId.Version == 7 && PackageId.Version == 7 &&
        ManifestSha256 is { Length: 64 } && ManifestSha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
        Sequence > 0 && OccurredAt != default && OccurredAt.Offset == TimeSpan.Zero && Enum.IsDefined(Result) &&
        (Failure is null || Enum.IsDefined(Failure.Value)) &&
        (Result is AgentUpdateResultKind.RolledBack or AgentUpdateResultKind.RollbackFailed or AgentUpdateResultKind.Failed || !RetryAllowed && Failure is null);
}

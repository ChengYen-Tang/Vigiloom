using System.Data.Common;

namespace Vigiloom.Contracts.Agent.Integrity;

/// <summary>Central progress for one scope belonging to the authenticated Agent.</summary>
public sealed record IntegrityScopeStatus(Guid ScopeId, bool ScopePresent, long? CurrentRevision, bool? Enabled,
    long HighWaterGeneration, long? ActiveGeneration, bool OtherScopeActive,
    long? LastAttemptGeneration, IntegrityScanOutcome? LastOutcome, IntegrityCompletion? LastStatus,
    Guid? LastSnapshotId, IntegrityPartialReason? LastPartialReason, bool? LastCoverageGap);

/// <summary>Reads progress using the caller's authenticated Agent transaction.</summary>
public interface IIntegrityStatusReader
{
    Task<IntegrityScopeStatus> ReadAsync(DbConnection connection, DbTransaction transaction, IntegrityOwner owner,
        Guid scopeId, CancellationToken token);
}
